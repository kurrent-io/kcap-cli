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

    sealed class Harness {
        public readonly FakeKcapCli Cli = new();
        public IReadOnlyList<HarnessId> Scope = [HarnessId.Claude, HarnessId.Cursor];
        public readonly HistoryStepViewModel Vm;

        public Harness(ImportDiscoveryReport? report) {
            Cli.DiscoverBehavior = _ => Task.FromResult(report);
            Vm = new HistoryStepViewModel(Cli, () => Scope, action => action(), "test-mac", TimeProvider.System);
        }

        public async Task EnterAsync() {
            await Vm.OnEnterAsync(CancellationToken.None);
            await Vm.Discovery;
        }

        public HistoryRepoRow Repo(string slug) => Vm.Groups.SelectMany(g => g.Repos).Single(r => r.Slug == slug);
    }

    [Test]
    public async Task Discovery_asks_for_the_harnesses_in_scope_and_groups_owners_by_recent_activity() {
        var h = new Harness(Report());
        await h.EnterAsync();

        await Assert.That(h.Cli.DiscoverCalls.Single()).IsEquivalentTo(["--claude", "--cursor"], CollectionOrdering.Matching);
        await Assert.That(h.Vm.State).IsEqualTo(HistoryState.Repos);
        await Assert.That(h.Vm.Groups.Select(g => g.Owner)).IsEquivalentTo(["acme", "solo"], CollectionOrdering.Matching);
        await Assert.That(h.Vm.FromLabels).IsEquivalentTo(["Claude Code", "Cursor"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Every_repository_starts_shared_over_the_last_ninety_days() {
        var h = new Harness(Report());
        await h.EnterAsync();

        await Assert.That(h.Vm.Groups.SelectMany(g => g.Repos).All(r => r.Level == ImportLevel.Shared)).IsTrue();
        await Assert.That(h.Vm.Windows.Single(w => w.IsSelected).Label).IsEqualTo("Last 90 days");
        await Assert.That(h.Vm.NextLabel).IsEqualTo("Import 3 repositories");
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

    [Test]
    public async Task A_window_a_repository_did_not_report_shows_no_count_rather_than_zero() {
        var report = new ImportDiscoveryReport(
            [new ImportDiscoveryRepo("acme", "web", 40, null, null)], 0, [new(D30, 6), new(D90, 27), new(null, 53)]);
        var h = new Harness(report);
        await h.EnterAsync();

        await Assert.That(h.Vm.Windows.All(w => w.CountLabel is null)).IsTrue();
    }

    /// The owner track is a bulk set; it reads a level back only when every repository agrees.
    [Test]
    public async Task The_owner_track_sets_every_repository_and_reads_mixed_when_they_disagree() {
        var h = new Harness(Report());
        await h.EnterAsync();
        var acme = h.Vm.Groups.Single(g => g.Owner == "acme");

        h.Repo("acme/api").Level = ImportLevel.OnlyMe;
        var mixed = (acme.Mixed, acme.Stop, acme.SignalLine);

        acme.Stop = (int)ImportLevel.Skip;

        await Assert.That(mixed).IsEqualTo((true, -1, "2 repositories · mixed"));
        await Assert.That(acme.Repos.All(r => r.Level == ImportLevel.Skip)).IsTrue();
        await Assert.That(acme.Mixed).IsFalse();
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
        await Assert.That(h.Vm.Run!.State).IsEqualTo(ImportRunState.Finished);
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
    public async Task No_harness_recording_has_nothing_to_discover() {
        var h = new Harness(Report()) { Scope = [] };
        await h.EnterAsync();

        await Assert.That(h.Vm.State).IsEqualTo(HistoryState.NoHarness);
        await Assert.That(h.Cli.DiscoverCalls).IsEmpty();
        await Assert.That(h.Vm.NextLabel).IsEqualTo("Carry on");
        await Assert.That(h.Vm.Skippable).IsFalse();
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
        gate.SetResult(Report());
        await h.Vm.Discovery;

        await Assert.That(looking).IsTrue();
        await Assert.That(h.Vm.HasRepos).IsTrue();
    }

    [Test]
    public async Task Titling_is_offered_only_for_a_harness_that_titles() {
        var h = new Harness(Report()) { Scope = [HarnessId.Cursor] };
        await h.EnterAsync();

        await Assert.That(h.Vm.TitlingOffered).IsFalse();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_pane_draws_a_track_per_owner_and_per_repository() {
        var tracks = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness(Report());
            var vm = new OnboardingViewModel([h.Vm, new DoneStepViewModel(() => DoneFacts.Empty)]);
            await vm.PendingEnterForTesting;
            await h.Vm.Discovery;

            var window = new MainWindow { Onboarding = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var count = window.GetVisualDescendants().OfType<ImportTrack>().Count();

            window.Close();
            Dispatcher.UIThread.RunJobs();

            return count;
        });

        await Assert.That(tracks).IsEqualTo(5); // two owners, three repositories
    }
}
