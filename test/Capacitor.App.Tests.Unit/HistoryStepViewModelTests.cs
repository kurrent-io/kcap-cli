using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.App.Views;
using Capacitor.App.Views.Onboarding;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

public class HistoryStepViewModelTests {
    static readonly DateOnly D30 = new(2026, 9, 1);
    static readonly DateOnly D90 = new(2026, 7, 3);

    static ImportDiscoveryRepo Repo(string owner, string name, int all, int d90, int d30, int day) =>
        new(owner, name, all, new DateTimeOffset(2026, 9, day, 9, 0, 0, TimeSpan.Zero),
            [new(D30, d30), new(D90, d90), new(null, all)]);

    static ImportDiscoveryReport Report(int unmatched = 0) => new([
        Repo("acme", "web", 40, 20, 5, 30),
        Repo("acme", "api", 10, 4, 1, 20),
        Repo("solo", "notes", 3, 3, 0, 25),
    ], unmatched, [new(D30, 6), new(D90, 27), new(null, 53)]);

    /// One harness's share of a repository, its windows in the report's order.
    static ImportDiscoveryVendor Share(string vendor, int all, int d90, int d30, int day) =>
        new(vendor, all, new DateTimeOffset(2026, 9, day, 9, 0, 0, TimeSpan.Zero), [new(D30, d30), new(D90, d90), new(null, all)]);

    static ImportDiscoveryRepo Split(string owner, string name, params ImportDiscoveryVendor[] shares) =>
        new(owner, name, shares.Sum(s => s.Sessions), shares.Max(s => s.LastSessionAt),
            [.. shares[0].Windows!.Select((w, i) => new ImportDiscoveryWindow(w.Since, shares.Sum(s => s.Windows![i].Sessions)))],
            shares);

    /// A machine whose Claude Code and Codex have written history and whose Pi has not: what a setup that
    /// turns recording on for Pi alone finds.
    static ImportDiscoveryReport OlderClaudeAndCodexHistory() => new([
        Split("acme", "web", Share("claude", 30, 15, 4, 30), Share("codex", 10, 5, 1, 28)),
        Split("acme", "api", Share("codex", 6, 2, 0, 20)),
        Split("solo", "notes", Share("claude", 3, 3, 0, 25)),
    ], 0, [new(D30, 5), new(D90, 25), new(null, 49)]);

    sealed class Harness {
        public readonly FakeKcapCli Cli = new();
        public bool Local;
        public readonly HistoryStepViewModel Vm;

        public Harness(ImportDiscoveryReport? report, bool onLocalPath = false) {
            Cli.DiscoverBehavior = _ => Task.FromResult(report);
            Local = onLocalPath;
            Vm = new HistoryStepViewModel(Cli, action => action(), "test-mac", TimeProvider.System, () => Local);
        }

        public async Task EnterAsync() {
            await Vm.OnEnterAsync(CancellationToken.None);
            await Vm.Discovery;
        }

        public HistoryRepoRow Repo(string slug) => Vm.Groups.SelectMany(g => g.Repos).Single(r => r.Slug == slug);
    }

    /// No vendor flag, so the CLI reads every harness on the machine; coming back to the page keeps
    /// what it found and the choices made on it.
    [Test]
    public async Task Discovery_asks_every_harness_once_and_groups_owners_by_recent_activity() {
        var h = new Harness(Report());
        await h.EnterAsync();
        await h.EnterAsync();

        await Assert.That(h.Cli.DiscoverCalls.Single()).IsEmpty();
        await Assert.That(h.Vm.State).IsEqualTo(HistoryState.Repos);
        await Assert.That(h.Vm.Groups.Select(g => g.Owner)).IsEquivalentTo(["acme", "solo"], CollectionOrdering.Matching);
    }

    /// A Pi-only setup still offers the Claude Code and Codex history already on disk, each harness its
    /// own checked choice, and importing it touches neither harness's recording or tools.
    [Test]
    public async Task A_new_Pi_setup_is_offered_older_Claude_and_Codex_history() {
        var h = new Harness(OlderClaudeAndCodexHistory());
        await h.EnterAsync();

        var offered = h.Vm.Sources.Select(s => (s.Label, s.Sessions, s.IsSelected)).ToList();
        await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
        await h.Vm.Run!.Completion;

        await Assert.That(offered).IsEquivalentTo([("Claude Code", 33, true), ("Codex", 16, true)], CollectionOrdering.Matching);
        await Assert.That(h.Vm.SourcesVisible).IsTrue();
        await Assert.That(h.Vm.FoundLine).IsEqualTo("Found 49 sessions in 3 repositories on test-mac");
        await Assert.That(h.Vm.TitlingOffered).IsTrue();
        await Assert.That(h.Cli.ImportRequests.Single().VendorFlags).IsEquivalentTo(["--claude", "--codex"], CollectionOrdering.Matching);
        await Assert.That(h.Cli.ImportRequests.Single().Repos!).IsEquivalentTo(["acme/web", "acme/api", "solo/notes"], CollectionOrdering.Matching);
        await Assert.That(h.Cli.PluginInstallCallCount).IsEqualTo(0);
        await Assert.That(h.Cli.AccountsCalls).IsEmpty();
    }

    /// An unchecked harness leaves the import and every count; a repository only it wrote to leaves the
    /// list, and the ones that stay keep the choices made on them.
    [Test]
    public async Task An_unchecked_harness_is_neither_counted_nor_imported() {
        var h = new Harness(OlderClaudeAndCodexHistory());
        await h.EnterAsync();
        h.Repo("solo/notes").OnlyMe = true;

        h.Vm.Sources.Single(s => s.Id == HarnessId.Codex).IsSelected = false;
        await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
        await h.Vm.Run!.Completion;

        await Assert.That(h.Vm.Groups.SelectMany(g => g.Repos).Select(r => r.Slug)).IsEquivalentTo(["acme/web", "solo/notes"], CollectionOrdering.Matching);
        await Assert.That(h.Repo("acme/web").Sessions).IsEqualTo(30);
        await Assert.That(h.Repo("solo/notes").Level).IsEqualTo(ImportLevel.OnlyMe);
        await Assert.That(h.Vm.Windows.Select(w => w.CountLabel ?? "").ToList())
            .IsEquivalentTo(["4 sessions", "18 sessions", "33 sessions"], CollectionOrdering.Matching);
        await Assert.That(h.Vm.FoundLine).IsEqualTo("Found 49 sessions in 3 repositories on test-mac");
        await Assert.That(h.Cli.ImportRequests.All(r => r.VendorFlags.SequenceEqual(["--claude"]))).IsTrue();
        await Assert.That(h.Cli.ImportRequests.SelectMany(r => r.Repos!)).IsEquivalentTo(["acme/web", "solo/notes"]);
    }

    [Test]
    public async Task With_every_harness_unchecked_nothing_imports() {
        var h = new Harness(OlderClaudeAndCodexHistory());
        await h.EnterAsync();

        foreach (var source in h.Vm.Sources) source.IsSelected = false;
        var left = await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);

        await Assert.That(left).IsTrue();
        await Assert.That(h.Vm.HasRepos).IsTrue();
        await Assert.That(h.Vm.SourcesVisible).IsTrue();
        await Assert.That(h.Vm.ImportLine).IsEqualTo("Nothing is selected to import");
        await Assert.That(h.Vm.NextLabel).IsEqualTo("Continue");
        await Assert.That(h.Vm.TitlingOffered).IsFalse();
        await Assert.That(h.Cli.ImportCallCount).IsEqualTo(0);
    }

    /// A folder pass carries no harness of its own, so with no harness left it would read every one.
    [Test]
    public async Task With_every_harness_unchecked_a_checked_folder_imports_nothing() {
        var h = new Harness(OlderClaudeAndCodexHistory() with {
            UnmatchedSessions = 3, UnmatchedFolders = [new("/w/scratch", 3, [new(D30, 1), new(D90, 2), new(null, 3)])],
        });
        await h.EnterAsync();

        h.Vm.Folders.Single().Included = true;
        foreach (var source in h.Vm.Sources) source.IsSelected = false;
        await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);

        await Assert.That(h.Vm.SelectedFolderCount).IsEqualTo(0);
        await Assert.That(h.Vm.ImportLine).IsEqualTo("Nothing is selected to import");
        await Assert.That(h.Vm.NextLabel).IsEqualTo("Continue");
        await Assert.That(h.Cli.ImportCallCount).IsEqualTo(0);
    }

    /// A repository that leaves the list with its harness comes back with the choices made on it.
    [Test]
    public async Task A_repository_keeps_its_choices_when_its_harness_is_unchecked_and_checked_again() {
        var h = new Harness(OlderClaudeAndCodexHistory());
        await h.EnterAsync();
        h.Repo("solo/notes").OnlyMe = true;
        h.Repo("acme/api").Included = false;

        foreach (var source in h.Vm.Sources) source.IsSelected = false;
        foreach (var source in h.Vm.Sources) source.IsSelected = true;

        await Assert.That(h.Repo("solo/notes").Level).IsEqualTo(ImportLevel.OnlyMe);
        await Assert.That(h.Repo("acme/api").Level).IsEqualTo(ImportLevel.Skip);
        await Assert.That(h.Repo("acme/web").Level).IsEqualTo(ImportLevel.Shared);
    }

    /// A report that does not split repositories by harness offers no harness choice and imports with no
    /// vendor flag, the same every-harness scope it counted.
    [Test]
    public async Task A_report_without_harness_shares_imports_what_it_counted() {
        var h = new Harness(Report());
        await h.EnterAsync();

        await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
        await h.Vm.Run!.Completion;

        await Assert.That(h.Vm.Sources).IsEmpty();
        await Assert.That(h.Vm.SourcesVisible).IsFalse();
        await Assert.That(h.Cli.ImportRequests.Single().VendorFlags).IsEmpty();
    }

    /// The CLI reports a harness that has never run, or a machine with no harness at all, as an empty
    /// report: no history, never unreadable.
    [Test]
    public async Task An_empty_machine_has_no_history_to_import() {
        var h = new Harness(new ImportDiscoveryReport([], 0, [new(D30, 0), new(D90, 0), new(null, 0)]));
        await h.EnterAsync();

        var left = await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);

        await Assert.That(h.Vm.State).IsEqualTo(HistoryState.NoHistory);
        await Assert.That(h.Vm.EmptyMessage).IsEqualTo("No history on test-mac to import. New sessions will be stored as they happen.");
        await Assert.That(h.Vm.Sources).IsEmpty();
        await Assert.That(left).IsTrue();
        await Assert.That(h.Cli.ImportCallCount).IsEqualTo(0);
    }

    [Test]
    public async Task Every_repository_starts_shared_over_the_last_ninety_days() {
        var h = new Harness(Report());
        await h.EnterAsync();

        await Assert.That(h.Vm.Groups.SelectMany(g => g.Repos).All(r => r.Level == ImportLevel.Shared)).IsTrue();
        await Assert.That(h.Vm.Windows.Single(w => w.IsSelected).Label).IsEqualTo("Last 90 days");
        await Assert.That(h.Vm.NextLabel).IsEqualTo("Import 3 repositories");
    }

    /// The local edition has no other readers: a repository imports or not, and an import is private.
    [Test]
    public async Task On_the_local_path_every_repository_imports_privately_with_no_shared_choice() {
        var h = new Harness(Report(), onLocalPath: true);
        await h.EnterAsync();
        var acme = h.Vm.Groups.Single(g => g.Owner == "acme");
        acme.OnlyMe = false;
        h.Repo("solo/notes").Level = ImportLevel.Shared;

        await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
        await h.Vm.Run!.Completion;

        await Assert.That(h.Vm.LocalLevels).IsTrue();
        await Assert.That(h.Vm.SharingLine).IsNull();
        await Assert.That(h.Vm.Groups.SelectMany(g => g.Repos).All(r => r is { Level: ImportLevel.OnlyMe, Local: true })).IsTrue();
        await Assert.That(h.Cli.ImportRequests.Single().Private).IsTrue();
        await Assert.That(h.Cli.ImportRequests.Single().Repos!).IsEquivalentTo(["acme/web", "acme/api", "solo/notes"], CollectionOrdering.Matching);
    }

    /// A window's count is the sum of its cell for every repository not skipped.
    [Test]
    public async Task Window_counts_follow_the_selected_repositories() {
        var h = new Harness(Report());
        await h.EnterAsync();
        h.Repo("acme/web").Level = ImportLevel.Skip;

        await Assert.That(h.Vm.Windows.Select(w => w.CountLabel ?? "").ToList())
            .IsEquivalentTo(["1 session", "7 sessions", "13 sessions"], CollectionOrdering.Matching);
        await Assert.That(h.Vm.NextLabel).IsEqualTo("Import 2 repositories");
    }

    /// The page says what it found and what Next will import, and the second line follows the choices under Customize.
    [Test]
    public async Task The_summary_follows_the_repositories_and_window_chosen() {
        var h = new Harness(Report());
        await h.EnterAsync();
        var found = h.Vm.FoundLine;
        var byDefault = h.Vm.ImportLine;

        h.Repo("acme/web").Level = ImportLevel.Skip;
        foreach (var w in h.Vm.Windows) w.IsSelected = w.Since is null; // the pane's radio group does this
        var narrowed = h.Vm.ImportLine;

        var sharedNoted = h.Vm.SharingLine;
        foreach (var repo in h.Vm.Groups.SelectMany(g => g.Repos)) repo.Level = ImportLevel.OnlyMe;
        var privateNoted = h.Vm.SharingLine;
        foreach (var repo in h.Vm.Groups.SelectMany(g => g.Repos)) repo.Level = ImportLevel.Skip;

        await Assert.That(sharedNoted).IsNotNull();
        await Assert.That(privateNoted).IsNull();
        await Assert.That(found).IsEqualTo("Found 53 sessions in 3 repositories on test-mac");
        await Assert.That(byDefault).IsEqualTo("kcap imports 27 sessions from all 3 repositories, covering the last 90 days");
        await Assert.That(narrowed).IsEqualTo("kcap imports 13 sessions from 2 of 3 repositories, covering your whole history");
        await Assert.That(h.Vm.ImportLine).IsEqualTo("Nothing is selected to import");
    }

    [Test]
    public async Task A_window_a_repository_did_not_report_shows_no_count_rather_than_zero() {
        var report = new ImportDiscoveryReport(
            [new ImportDiscoveryRepo("acme", "web", 40, null, null)], 0, [new(D30, 6), new(D90, 27), new(null, 53)]);
        var h = new Harness(report);
        await h.EnterAsync();

        await Assert.That(h.Vm.Windows.All(w => w.CountLabel is null)).IsTrue();
        await Assert.That(h.Vm.ImportLine).IsEqualTo("kcap imports the last 90 days from the one repository");
    }

    /// The owner's boxes are bulk sets that read back a value only while its repositories agree, and a
    /// repository checked again keeps the audience it had.
    [Test]
    public async Task The_owner_boxes_set_every_repository_and_read_mixed_when_they_disagree() {
        var h = new Harness(Report());
        await h.EnterAsync();
        var acme = h.Vm.Groups.Single(g => g.Owner == "acme");

        h.Repo("acme/api").OnlyMe = true;
        var mixedAudience = (acme.Included, acme.OnlyMe, acme.SignalLine);
        h.Repo("acme/web").Included = false;
        var mixedImport = (acme.Included, acme.OnlyMe);

        acme.Included = false;
        var none = (acme.Included, acme.OnlyMeEnabled);

        var includedRaised = 0;
        foreach (var repo in acme.Repos)
            repo.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(HistoryRepoRow.Included)) includedRaised++; };
        acme.Included = true;
        var raisedOnCheck = includedRaised;
        var restored = (h.Repo("acme/web").Level, h.Repo("acme/api").Level);

        acme.OnlyMe = true;

        await Assert.That(mixedAudience).IsEqualTo((true, (bool?)null, "2 repositories · mixed"));
        await Assert.That(mixedImport).IsEqualTo(((bool?)null, (bool?)true));
        await Assert.That(none).IsEqualTo(((bool?)false, false));
        // The row's box binds Included. A Level write that does not raise it leaves the row unchecked.
        await Assert.That(raisedOnCheck).IsEqualTo(acme.Repos.Count);
        await Assert.That(restored).IsEqualTo((ImportLevel.Shared, ImportLevel.OnlyMe));
        await Assert.That(acme.Repos.All(r => r.Level == ImportLevel.OnlyMe)).IsTrue();
        await Assert.That(acme.Mixed).IsFalse();
    }

    /// The owner's Only me reaches a repository skipped at the time, so checking it later does not bring
    /// it back shared.
    [Test]
    public async Task Only_me_on_the_owner_carries_to_a_skipped_repository() {
        var h = new Harness(Report());
        await h.EnterAsync();
        var acme = h.Vm.Groups.Single(g => g.Owner == "acme");

        h.Repo("acme/web").Included = false;
        acme.OnlyMe = true;
        var whileSkipped = h.Repo("acme/web").Level;
        h.Repo("acme/web").Included = true;

        await Assert.That(whileSkipped).IsEqualTo(ImportLevel.Skip);
        await Assert.That(h.Repo("acme/web").Level).IsEqualTo(ImportLevel.OnlyMe);
    }

    /// --private is per invocation, so each level is its own pass, with the window and titling choice.
    [Test]
    public async Task Next_starts_one_pass_per_level_and_moves_on_without_waiting() {
        var h = new Harness(Report());
        var gate = new TaskCompletionSource<StreamingResult>();
        h.Cli.ImportBehavior = (_, _, _) => gate.Task;
        await h.EnterAsync();
        h.Repo("solo/notes").Level = ImportLevel.OnlyMe;
        h.Vm.TitleLocally = false;
        var sharing = new List<string?>();
        h.Vm.PropertyChanged += (_, e) => {
            if (e.PropertyName == nameof(HistoryStepViewModel.SharingLine)) sharing.Add(h.Vm.SharingLine);
        };

        var left = await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
        var first = h.Cli.ImportRequests.Single();
        gate.SetResult(new StreamingResult(0, false, []));
        await h.Vm.Run!.Completion;

        await Assert.That(left).IsTrue();
        await Assert.That(first.Repos!).IsEquivalentTo(["solo/notes"]);
        await Assert.That(first.Private).IsTrue();
        await Assert.That(first.Since).IsEqualTo(D90);
        await Assert.That(first.SkipTitle).IsTrue();
        await Assert.That(h.Cli.ImportRequests[1].Repos!).IsEquivalentTo(["acme/web", "acme/api"], CollectionOrdering.Matching);
        await Assert.That(h.Cli.ImportRequests[1].Private).IsFalse();
        await Assert.That(h.Vm.Run!.Expected).IsEqualTo(27);
        await Assert.That(sharing.Last()).IsEqualTo("Shared imports follow your recording privacy setting");
    }

    [Test]
    public async Task The_run_counts_each_passes_closing_line() {
        var h = new Harness(Report());
        h.Cli.ImportBehavior = (_, onLine, _) => {
            onLine(new StreamedLine(ProcessStreamKind.Stdout, "  12 imported · 3 skipped · 1 failed"));
            return Task.FromResult(new StreamingResult(0, false, []));
        };
        await h.EnterAsync();
        h.Repo("solo/notes").Level = ImportLevel.OnlyMe;

        await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
        await h.Vm.Run!.Completion;

        await Assert.That(h.Vm.Run!.Imported).IsEqualTo(24);
        await Assert.That(h.Vm.Run!.Failed).IsEqualTo(2);
        await Assert.That(h.Vm.Run!.State).IsEqualTo(ImportRunState.Failed);
    }

    /// The figures move as each session settles, and the closing line still settles the totals.
    [Test]
    public async Task The_run_counts_sessions_as_they_land() {
        var h = new Harness(Report());
        (int Imported, int Failed, int Processed)? midway = null;
        h.Cli.ImportBehavior = (_, onLine, _) => {
            foreach (var text in (string[]) [
                         "Loading 1a2b... 120 lines [new]", "Loading 3c4d (cursor)", "Skipping 5e6f (already current)",
                         "Already loaded 7a8b (no new content)", "Failed 9c0d", "Skipping 1e2f [transcript unreadable]",
                         "Failed to reach the server", "↳ imported subagent 3a4b (12 lines)",
                     ])
                onLine(new StreamedLine(ProcessStreamKind.Stdout, $"  {text}"));
            var run = h.Vm.Run!;
            midway ??= (run.Imported, run.Failed, run.Processed);
            onLine(new StreamedLine(ProcessStreamKind.Stdout, "  3 imported · 2 skipped · 3 failed"));
            return Task.FromResult(new StreamingResult(0, false, []));
        };
        await h.EnterAsync();
        h.Repo("solo/notes").Level = ImportLevel.OnlyMe;

        await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
        await h.Vm.Run!.Completion;

        await Assert.That(midway).IsEqualTo((2, 2, 6));
        await Assert.That(h.Vm.Run!.Imported).IsEqualTo(6);
        await Assert.That(h.Vm.Run!.Failed).IsEqualTo(6);
        await Assert.That(h.Vm.Run!.Processed).IsEqualTo(16);
    }

    /// A pass that dies before its totals line keeps what it counted, and the next pass's totals add to it.
    [Test]
    public async Task A_pass_without_totals_keeps_its_live_counts() {
        var h = new Harness(Report());
        var pass = 0;
        h.Cli.ImportBehavior = (_, onLine, _) => {
            if (pass++ == 0) {
                for (var i = 0; i < 5; i++) onLine(new StreamedLine(ProcessStreamKind.Stdout, $"  Loading s{i}... 10 lines [new]"));
                return Task.FromResult(new StreamingResult(1, false, []));
            }

            onLine(new StreamedLine(ProcessStreamKind.Stdout, "  Loading t0... 10 lines [new]"));
            onLine(new StreamedLine(ProcessStreamKind.Stdout, "  1 imported · 0 skipped · 0 failed"));
            return Task.FromResult(new StreamingResult(0, false, []));
        };
        await h.EnterAsync();
        h.Repo("solo/notes").Level = ImportLevel.OnlyMe;

        await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
        await h.Vm.Run!.Completion;

        await Assert.That(h.Vm.Run!.Imported).IsEqualTo(6);
    }

    /// A repository the report did not count makes the whole selection's count unknown, not a smaller one.
    [Test]
    public async Task An_uncounted_repository_leaves_the_expected_count_unknown() {
        var h = new Harness(new ImportDiscoveryReport([
            Repo("acme", "web", 40, 20, 5, 30),
            new ImportDiscoveryRepo("solo", "notes", 3, null, null),
        ], 0, [new(D30, 5), new(D90, 20), new(null, 43)]));
        await h.EnterAsync();
        h.Repo("solo/notes").Level = ImportLevel.OnlyMe;

        await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
        await h.Vm.Run!.Completion;

        await Assert.That(h.Vm.Run!.Expected).IsEqualTo(0);
    }

    /// The same three repositories, plus four sessions that matched none: three in a folder discovery can
    /// name and one with no folder recorded.
    static ImportDiscoveryReport ReportWithFolders() => Report(unmatched: 4) with {
        UnmatchedFolders = [new("/w/scratch", 3, [new(D30, 1), new(D90, 2), new(null, 3)]), new(null, 1)],
    };

    /// Every repository starts checked, so an untouched page imports every repository and no folder
    /// outside one, which waits unchecked under Customize.
    [Test]
    public async Task By_default_every_repository_imports_and_no_folder_outside_one_does() {
        var h = new Harness(ReportWithFolders());
        await h.EnterAsync();
        var hiddenUntilCustomizing = h.Vm.FoldersVisible;

        await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
        await h.Vm.Run!.Completion;

        await Assert.That(h.Vm.Groups.SelectMany(g => g.Repos).All(r => r.Included)).IsTrue();
        await Assert.That(h.Vm.Folders.Select(f => (f.Path, f.Included))).IsEquivalentTo([("/w/scratch", false)]);
        await Assert.That(hiddenUntilCustomizing).IsFalse();
        await Assert.That(h.Vm.ImportLine).IsEqualTo("kcap imports 27 sessions from all 3 repositories, covering the last 90 days");
        await Assert.That(h.Cli.ImportRequests.Single().Repos!).IsEquivalentTo(["acme/web", "acme/api", "solo/notes"], CollectionOrdering.Matching);
        await Assert.That(h.Cli.ImportRequests.Single().Folders).IsNull();
    }

    /// Unchecking a repository leaves it out of the import and nothing else: the request simply does not
    /// name it, and the page says the choice is about this import only.
    [Test]
    public async Task An_unchecked_repository_is_left_out_of_this_import_only() {
        var h = new Harness(Report());
        await h.EnterAsync();

        h.Repo("acme/api").Included = false;
        await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
        await h.Vm.Run!.Completion;

        await Assert.That(h.Cli.ImportRequests.Single().Repos!).IsEquivalentTo(["acme/web", "solo/notes"], CollectionOrdering.Matching);
        await Assert.That(HistoryStepViewModel.SelectionNote).Contains("this import only");
        await Assert.That(HistoryStepViewModel.SelectionNote).Contains("excluded_repos");
    }

    /// A checked folder imports beside the repositories, privately by default, and every count and label
    /// takes it in.
    [Test]
    public async Task A_checked_folder_imports_privately_beside_the_repositories() {
        var h = new Harness(ReportWithFolders());
        await h.EnterAsync();
        h.Vm.ToggleCustomizeCommand.Execute().Subscribe();

        h.Vm.Folders.Single().Included = true;
        var label = h.Vm.NextLabel;
        var line  = h.Vm.ImportLine;
        await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
        await h.Vm.Run!.Completion;

        var onlyMe = h.Cli.ImportRequests.Single(r => r.Private);
        var shared = h.Cli.ImportRequests.Single(r => !r.Private);
        await Assert.That(h.Vm.FoldersVisible).IsTrue();
        await Assert.That(label).IsEqualTo("Import 4 projects");
        await Assert.That(line).IsEqualTo("kcap imports 29 sessions from all 3 repositories and 1 folder, covering the last 90 days");
        await Assert.That(onlyMe.Repos!).IsEmpty();
        await Assert.That(onlyMe.Folders!).IsEquivalentTo(["/w/scratch"]);
        await Assert.That(shared.Folders).IsNull();
        await Assert.That(h.Vm.Run!.Expected).IsEqualTo(29);
    }

    /// A folder alone is enough to import, and on the cloud its Only me box can widen it like a repository's.
    [Test]
    public async Task A_folder_alone_imports_and_can_follow_the_privacy_setting() {
        var h = new Harness(ReportWithFolders());
        await h.EnterAsync();

        foreach (var repo in h.Vm.Groups.SelectMany(g => g.Repos)) repo.Included = false;
        var folder = h.Vm.Folders.Single();
        folder.Included = true;
        folder.OnlyMe   = false;
        var label = h.Vm.NextLabel;
        await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
        await h.Vm.Run!.Completion;

        await Assert.That(label).IsEqualTo("Import 1 project");
        await Assert.That(h.Vm.SharingLine).IsNotNull();
        var request = h.Cli.ImportRequests.Single();
        await Assert.That(request.Private).IsFalse();
        await Assert.That(request.Folders!).IsEquivalentTo(["/w/scratch"]);
    }

    /// The local edition keeps a checked folder private, as it does every repository.
    [Test]
    public async Task On_the_local_path_a_folder_stays_private() {
        var h = new Harness(ReportWithFolders(), onLocalPath: true);
        await h.EnterAsync();

        var folder = h.Vm.Folders.Single();
        folder.Included = true;
        folder.OnlyMe   = false;

        await Assert.That(folder.Level).IsEqualTo(ImportLevel.OnlyMe);
        await Assert.That(h.Vm.UnmatchedNote!).Contains("check their folders under Customize");
    }

    /// A folder's count is not split by harness, so with a harness unchecked it is unknown rather than wrong.
    [Test]
    public async Task A_folder_with_a_harness_unchecked_shows_no_count_rather_than_a_guess() {
        var h = new Harness(OlderClaudeAndCodexHistory() with {
            UnmatchedSessions = 3, UnmatchedFolders = [new("/w/scratch", 3, [new(D30, 1), new(D90, 2), new(null, 3)])],
        });
        await h.EnterAsync();

        h.Vm.Folders.Single().Included = true;
        var counted = h.Vm.Windows.Select(w => w.CountLabel).ToList();
        h.Vm.Sources.Single(s => s.Id == HarnessId.Codex).IsSelected = false;

        await Assert.That(counted.All(c => c is not null)).IsTrue();
        await Assert.That(h.Vm.Windows.All(w => w.CountLabel is null)).IsTrue();
    }

    [Test]
    public async Task Not_now_imports_nothing() {
        var h = new Harness(Report());
        await h.EnterAsync();

        await h.Vm.CanLeaveAsync(WizardNavigation.Skip, CancellationToken.None);

        await Assert.That(h.Cli.ImportCallCount).IsEqualTo(0);
        await Assert.That(h.Vm.Run).IsNull();
    }

    [Test]
    [Arguments(0, HistoryState.NoHistory)]
    [Arguments(12, HistoryState.OnlyUnmatched)]
    public async Task A_report_with_no_repositories_says_why(int unmatched, HistoryState expected) {
        var h = new Harness(new ImportDiscoveryReport([], unmatched, []));
        await h.EnterAsync();

        await Assert.That(h.Vm.State).IsEqualTo(expected);
        await Assert.That(h.Vm.EmptyMessage).IsNotNull();
    }

    /// Every line the local path can show, across each empty arm and the repository list.
    static async Task<List<string?>> LocalLinesAsync(bool onLocalPath) {
        var lines = new List<string?>();
        foreach (var report in new[] { new ImportDiscoveryReport([], 12, []), null }) {
            var h = new Harness(report, onLocalPath);
            await h.EnterAsync();
            lines.Add(h.Vm.EmptyMessage);
        }
        var repos = new Harness(Report(unmatched: 4), onLocalPath);
        await repos.EnterAsync();
        lines.Add(repos.Vm.UnmatchedNote);
        lines.Add(repos.Vm.DeclineNote);

        return lines;
    }

    [Test]
    public async Task The_local_path_names_no_terminal_command() {
        var lines = await LocalLinesAsync(onLocalPath: true);

        await Assert.That(lines.All(l => l is not null)).IsTrue();
        foreach (var line in lines) {
            await Assert.That(line!).DoesNotContain("kcap import");
            await Assert.That(line).DoesNotContain("kcap remap");
            await Assert.That(line).DoesNotContain("kcap setup");
        }
    }

    [Test]
    public async Task The_cloud_path_keeps_its_terminal_commands() {
        var lines = await LocalLinesAsync(onLocalPath: false);

        await Assert.That(lines.All(l => l is not null && (l.Contains("kcap import", StringComparison.Ordinal)
            || l.Contains("kcap remap", StringComparison.Ordinal)))).IsTrue();
    }

    [Test]
    public async Task A_failed_discovery_is_unreadable_not_empty() {
        var h = new Harness(null);
        await h.EnterAsync();

        await Assert.That(h.Vm.State).IsEqualTo(HistoryState.Unreadable);
    }

    [Test]
    public async Task A_discovery_that_could_not_read_the_disk_runs_again_on_the_next_visit() {
        var h = new Harness(null);
        await h.EnterAsync();
        h.Cli.DiscoverBehavior = _ => Task.FromResult<ImportDiscoveryReport?>(Report());
        await h.EnterAsync();

        await Assert.That(h.Cli.DiscoverCalls.Count).IsEqualTo(2);
        await Assert.That(h.Vm.HasRepos).IsTrue();
    }

    [Test]
    public async Task A_failed_import_starts_again_on_the_next_Next_and_a_finished_one_does_not() {
        var h = new Harness(Report());
        h.Cli.ImportBehavior = (_, _, _) => Task.FromResult(new StreamingResult(1, false, []));
        await h.EnterAsync();

        await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
        await h.Vm.Run!.Completion;
        var failed = h.Vm.Run!.State;
        h.Cli.ImportBehavior = (_, _, _) => Task.FromResult(new StreamingResult(0, false, []));
        await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
        await h.Vm.Run!.Completion;
        await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);

        await Assert.That(failed).IsEqualTo(ImportRunState.Failed);
        await Assert.That(h.Vm.Run!.State).IsEqualTo(ImportRunState.Finished);
        await Assert.That(h.Cli.ImportCallCount).IsEqualTo(2); // one shared pass per run, no third
    }

    /// Entry does not wait on discovery: the shell holds its buttons until entry returns.
    [Test]
    public async Task Entry_returns_before_discovery_finishes() {
        var h = new Harness(Report());
        var gate = new TaskCompletionSource<ImportDiscoveryReport?>();
        h.Cli.DiscoverBehavior = _ => gate.Task;

        await h.Vm.OnEnterAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        var looking = h.Vm.Looking;
        await Assert.That(h.Vm.CanContinue).IsFalse();
        await Assert.That(h.Vm.Skippable).IsTrue();
        await Assert.That(await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None)).IsFalse();
        await Assert.That(h.Cli.ImportCallCount).IsEqualTo(0);
        gate.SetResult(Report());
        await h.Vm.Discovery;

        await Assert.That(looking).IsTrue();
        await Assert.That(h.Vm.HasRepos).IsTrue();
    }

    [Test]
    public async Task Switching_edition_cancels_old_discovery_and_drops_its_result() {
        var h = new Harness(Report());
        var gate = new TaskCompletionSource<ImportDiscoveryReport?>();
        h.Cli.DiscoverBehavior = _ => gate.Task;
        await h.Vm.OnEnterAsync(CancellationToken.None);
        var oldDiscovery = h.Vm.Discovery;
        h.Local = true;
        h.Cli.DiscoverBehavior = _ => Task.FromResult<ImportDiscoveryReport?>(new([], 0, []));
        await h.EnterAsync();
        gate.SetResult(Report());
        await oldDiscovery;
        await Assert.That(h.Vm.State).IsEqualTo(HistoryState.NoHistory);
        await Assert.That(h.Vm.Groups).IsEmpty();
        await Assert.That(h.Vm.SelectedCount).IsEqualTo(0);
        await Assert.That(h.Cli.DiscoverCalls.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Import_timeout_is_a_retryable_failure_even_with_zero_exit_code() {
        var h = new Harness(Report());
        h.Cli.ImportBehavior = (_, _, _) => Task.FromResult(new StreamingResult(0, true, []));
        await h.EnterAsync();
        await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
        await h.Vm.Run!.Completion;
        await Assert.That(h.Vm.Run.State).IsEqualTo(ImportRunState.Failed);
        await Assert.That(h.Vm.ImportStarted).IsFalse();
        await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
        await h.Vm.Run.Completion;
        await Assert.That(h.Cli.ImportCallCount).IsEqualTo(2);
    }

    [Test]
    public async Task Titling_is_offered_only_while_a_harness_that_titles_is_imported_from() {
        var h = new Harness(new ImportDiscoveryReport(
            [Split("acme", "web", Share("claude", 4, 4, 1, 30), Share("cursor", 2, 2, 0, 29))], 0, []));
        await h.EnterAsync();
        var both = h.Vm.TitlingOffered;

        h.Vm.Sources.Single(s => s.Id == HarnessId.Claude).IsSelected = false;

        await Assert.That(both).IsTrue();
        await Assert.That(h.Vm.TitlingOffered).IsFalse();
    }

    /// Naming sends excerpts to the agent's model provider, so the box starts clear and an import the user
    /// did not turn it on for passes --skip-title; ticking it is the only way to name.
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Naming_is_off_until_the_user_turns_it_on(bool local) {
        var untouched = new Harness(Report(), onLocalPath: local);
        await untouched.EnterAsync();
        var offered = untouched.Vm.TitlingOffered;
        var ticked  = untouched.Vm.TitleLocally;
        await untouched.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
        await untouched.Vm.Run!.Completion;

        var opted = new Harness(Report(), onLocalPath: local);
        await opted.EnterAsync();
        opted.Vm.TitleLocally = true;
        await opted.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
        await opted.Vm.Run!.Completion;

        await Assert.That(offered).IsTrue();
        await Assert.That(ticked).IsFalse();
        await Assert.That(untouched.Cli.ImportRequests).IsNotEmpty();
        await Assert.That(opted.Cli.ImportRequests).IsNotEmpty();
        await Assert.That(untouched.Cli.ImportRequests.All(r => r.SkipTitle)).IsTrue();
        await Assert.That(opted.Cli.ImportRequests.All(r => !r.SkipTitle)).IsTrue();
    }

    /// "Everything stays on this machine" is true of a local import only while naming is off; turning it on
    /// changes the promise to say where the excerpts go.
    [Test]
    public async Task The_local_lede_promises_nothing_leaves_only_while_naming_is_off() {
        var h = new Harness(Report(), onLocalPath: true);
        await h.EnterAsync();
        var off    = h.Vm.Lede;
        var raised = new List<string?>();
        h.Vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        h.Vm.TitleLocally = true;

        await Assert.That(off).Contains("Everything stays on this machine.");
        await Assert.That(h.Vm.Lede).DoesNotContain("Everything stays on this machine.");
        await Assert.That(h.Vm.Lede).Contains("Naming sends excerpts to your agent's model provider");
        await Assert.That(raised).Contains(nameof(HistoryStepViewModel.Lede));
    }

    /// The checkboxes wait behind Customize; opening it draws an Import and an Only me box per owner and
    /// per repository. What Next would leave out or share stays in view either way.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_pane_draws_boxes_per_owner_and_per_repository_once_customizing() {
        var (before, after, notes) = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness(Report(unmatched: 4));
            var vm = new OnboardingViewModel([h.Vm, new DoneStepViewModel(() => DoneFacts.Empty)]);
            await vm.PendingEnterForTesting;
            await h.Vm.Discovery;

            var window = new MainWindow { Onboarding = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            int Shown() => window.GetVisualDescendants().OfType<CheckBox>()
                .Count(c => c.Name is "OwnerImportCheck" or "RepoImportCheck" or "OwnerOnlyMeCheck" or "RepoOnlyMeCheck" && c.IsEffectivelyVisible);
            var collapsed = Shown();
            var notesShown = window.GetVisualDescendants().OfType<TextBlock>()
                .Count(t => t.Name is "UnmatchedNoteText" or "SharingLineText" && t.IsEffectivelyVisible);
            window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "CustomizeButton").Command!.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var open = Shown();

            window.Close();
            Dispatcher.UIThread.RunJobs();

            return (collapsed, open, notesShown);
        });

        await Assert.That(before).IsEqualTo(0);
        await Assert.That(notes).IsEqualTo(2);
        await Assert.That(after).IsEqualTo(10);
    }

    /// A mixed Only me box is half-filled, and a click on it turns every repository private rather than
    /// widening them all to shared.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_click_on_a_mixed_only_me_box_keeps_every_repository_private() {
        var levels = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness(Report());
            await h.EnterAsync();
            h.Repo("acme/api").OnlyMe = true;
            h.Vm.ToggleCustomizeCommand.Execute().Subscribe();
            var window = new Window { Content = new HistoryPaneView { DataContext = h.Vm }, Width = 800, Height = 900 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var box = window.GetVisualDescendants().OfType<CheckBox>()
                .First(c => c.Name == "OwnerOnlyMeCheck" && c.DataContext is HistoryOwnerGroup { Owner: "acme" });
            var mixedShown = box.IsChecked;
            ((IToggleProvider)ControlAutomationPeer.CreatePeerForElement(box)!).Toggle();
            Dispatcher.UIThread.RunJobs();
            var result = (Mixed: mixedShown, Clicked: box.IsChecked,
                Levels: h.Vm.Groups.Single(g => g.Owner == "acme").Repos.Select(r => r.Level).ToList());

            window.Close();
            Dispatcher.UIThread.RunJobs();
            return result;
        });

        await Assert.That(levels.Mixed).IsNull();
        await Assert.That(levels.Clicked).IsTrue();
        await Assert.That(levels.Levels.All(l => l == ImportLevel.OnlyMe)).IsTrue();
    }
}
