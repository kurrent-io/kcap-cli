using System.Reactive.Threading.Tasks;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.App.Views;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

/// Owns ReactiveCommands (per-row Retry), so every test runs through the real headless session.
public class HarnessesStepViewModelTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    sealed class Harness {
        public readonly FakeKcapCli Cli = new();
        public readonly List<HarnessId> Stamped = [];
        public IReadOnlySet<string>? ProviderKeys = new HashSet<string>();
        public int DetectCalls;
        public readonly HarnessesStepViewModel Vm;

        public Harness(ConfigRoot config, IReadOnlyDictionary<HarnessId, DetectedAgent>? detected = null,
                IReadOnlySet<HarnessId>? declined = null, PathFixViewModel? pathFix = null, bool local = false,
                Func<CancellationToken, Task<VisibilityOptionsResponse?>>? visibilityOptions = null,
                Func<int, IReadOnlyDictionary<HarnessId, DetectedAgent>>? detectOnCall = null) {
            detected ??= new Dictionary<HarnessId, DetectedAgent> {
                [HarnessId.Claude] = new(true, true),
                [HarnessId.Cursor] = new(false, true),
                [HarnessId.Pi]     = new(true, false),
            };
            Vm = new HarnessesStepViewModel(
                Cli,
                _ => { DetectCalls++; return Task.FromResult(detectOnCall?.Invoke(DetectCalls) ?? detected); },
                () => declined ?? new HashSet<HarnessId>(),
                Stamped.AddRange,
                config, pathFix,
                _ => Task.FromResult<IReadOnlySet<string>?>(ProviderKeys),
                "test-mac",
                onLocalPath: local ? () => true : null,
                visibilityOptions: visibilityOptions);
        }

        public HarnessRowViewModel Row(string label) => Vm.Rows.First(r => r.Label == label);
    }

    string ConfigPath => AppConfig.GetConfigPath(Config.Root);

    [Test]
    [Arguments(WizardNavigation.Next)]
    [Arguments(WizardNavigation.Skip)]
    [NotInParallel("AvaloniaSession")]
    public async Task Privacy_requires_an_explicit_choice_before_install_or_skip(WizardNavigation direction) {
        await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness(Config.Root);
            await h.Vm.OnEnterAsync(CancellationToken.None);
            await Assert.That(h.Vm.Visibility).IsNull();
            await Assert.That(h.Vm.CanContinue).IsFalse();
            await Assert.That(h.Vm.CanSkip).IsFalse();
            await Assert.That(await h.Vm.CanLeaveAsync(direction, CancellationToken.None)).IsFalse();
            await Assert.That(h.Cli.PluginInstallCallCount).IsEqualTo(0);
            await Assert.That(h.Stamped).IsEmpty();
            await Assert.That(File.Exists(ConfigPath)).IsFalse();
            await Assert.That(await h.Vm.CanLeaveAsync(WizardNavigation.Back, CancellationToken.None)).IsTrue();

            h.Vm.Visibility = "invalid";
            await Assert.That(await h.Vm.CanLeaveAsync(direction, CancellationToken.None)).IsFalse();
            await Assert.That(File.Exists(ConfigPath)).IsFalse();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Navigation_stays_visible_in_a_short_window_and_the_next_page_starts_at_the_top() {
        await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness(Config.Root);
            h.Cli.DiscoverBehavior = _ => Task.FromResult<ImportDiscoveryReport?>(new(
                [.. Enumerable.Range(0, 20).Select(i => new ImportDiscoveryRepo("org", $"repo-{i}", 3, null, null))], 0, []));
            var history = new HistoryStepViewModel(h.Cli, a => a(), "test-mac", TimeProvider.System);
            var vm = new OnboardingViewModel([h.Vm, history]);
            await vm.PendingEnterForTesting;
            var window = new MainWindow { Onboarding = vm, Width = 1024, Height = 640 };
            window.Show();
            try {
                Dispatcher.UIThread.RunJobs();
                var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "StepScroll");
                var next = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "NextButton");
                var skip = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SkipButton");
                var privacy = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PrivacyCard");
                var rows = window.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Name == "HarnessRows");
                await Assert.That(privacy.TranslatePoint(default, window)!.Value.Y < rows.TranslatePoint(default, window)!.Value.Y).IsTrue();
                await Assert.That(next.IsEnabled).IsFalse();
                await Assert.That(skip.IsEnabled).IsFalse();
                await Assert.That(window.GetVisualDescendants().OfType<RadioButton>().Any(r => r.IsChecked == true)).IsFalse();
                var position = next.TranslatePoint(default, window)!.Value;
                await Assert.That(position.Y + next.Bounds.Height <= window.Bounds.Height).IsTrue();
                var selected = window.GetVisualDescendants().OfType<RadioButton>().Single(r => r.Tag as string == "private");
                selected.IsChecked = true;
                Dispatcher.UIThread.RunJobs();
                await Assert.That(selected.IsChecked).IsTrue();
                await Assert.That(h.Vm.Visibility).IsEqualTo("private");
                await Assert.That(next.IsEnabled).IsTrue();
                await Assert.That(skip.IsEnabled).IsTrue();
                scroll.Offset = new Vector(0, 100);
                Dispatcher.UIThread.RunJobs();
                await Assert.That(scroll.Offset.Y > 0).IsTrue();
                await vm.NextCommand.Execute().ToTask();
                await vm.NextCommand.Execute().ToTask();
                await history.Discovery;
                Dispatcher.UIThread.RunJobs();
                await Assert.That(scroll.Offset.Y).IsEqualTo(0);
            } finally {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Rows_are_the_detected_harnesses_in_registry_order_with_both_answers_on() {
        var (labels, signals, answers, notFound) = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness(Config.Root);
            await h.Vm.OnEnterAsync(CancellationToken.None);

            return (h.Vm.Rows.Select(r => r.Label).ToList(), h.Vm.Rows.Select(r => r.SignalLine).ToList(),
                h.Vm.Rows.Select(r => (r.Record, r.Tools)).ToList(), h.Vm.NotFoundLine);
        });

        await Assert.That(labels).IsEquivalentTo(["Claude Code", "Cursor", "Pi"], CollectionOrdering.Matching);
        await Assert.That(signals).IsEquivalentTo(["on your PATH", "settings folder only", "on your PATH"], CollectionOrdering.Matching);
        await Assert.That(answers).IsEquivalentTo([(true, true), (false, false), (true, true)], CollectionOrdering.Matching);
        await Assert.That(notFound).StartsWith("Not found: Codex, Copilot");
    }

    /// A settings folder alone, Pi's agent directory included, is no consent to record: those rows start
    /// off and the page says why, while an installed app still starts on. Next installs nothing for them.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Harnesses_found_only_by_a_settings_folder_start_off_and_the_page_says_why() {
        var (answers, note, calls) = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness(Config.Root, new Dictionary<HarnessId, DetectedAgent> {
                [HarnessId.Claude]  = new(false, true, DesktopAppFound: true),
                [HarnessId.Cursor]  = new(false, true),
                [HarnessId.Copilot] = new(false, true),
                [HarnessId.Gemini]  = new(false, true),
                [HarnessId.Pi]      = new(false, true),
            });
            await h.Vm.OnEnterAsync(CancellationToken.None);
            h.Vm.Visibility = "private";
            var answers = h.Vm.Rows.ToDictionary(r => r.Label, r => (r.Record, r.Tools));
            var note = h.Vm.SettingsOnlyNote;
            await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);

            return (answers, note, h.Cli.PluginInstallCalls.ToList());
        });

        await Assert.That(answers["Claude Code"]).IsEqualTo((true, true));
        foreach (var label in new[] { "Copilot", "Cursor", "Gemini", "Pi" })
            await Assert.That(answers[label]).IsEqualTo((false, false));
        await Assert.That(note).IsEqualTo(HarnessesStepViewModel.SettingsOnlyNoteFor(["Cursor", "Copilot", "Gemini", "Pi"]));
        await Assert.That(calls).IsEquivalentTo([(string?)null], CollectionOrdering.Matching);
    }

    [Test]
    public async Task The_settings_only_note_names_each_row_it_explains() {
        await Assert.That(HarnessesStepViewModel.SettingsOnlyNoteFor([])).IsNull();
        await Assert.That(HarnessesStepViewModel.SettingsOnlyNoteFor(["Cursor"]))
            .IsEqualTo("Only Cursor's settings folder was found, so it starts off. Turn it on if you use it.");
        await Assert.That(HarnessesStepViewModel.SettingsOnlyNoteFor(["Cursor", "Copilot", "Gemini"]))
            .IsEqualTo("Only the settings folders of Cursor, Copilot and Gemini were found, so they start off. Turn on the ones you use.");
    }

    /// No tools found sends the user to Settings → Harnesses after setup, not back through the wizard.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Nothing_found_points_at_a_relaunch_and_nothing_chosen_at_harnesses_settings() {
        var (noneFound, declineVisible) = await AvaloniaSession.DispatchAsync(async () => {
            var none = new Harness(Config.Root, new Dictionary<HarnessId, DetectedAgent>());
            await none.Vm.OnEnterAsync(CancellationToken.None);
            var unchosen = new Harness(Config.Root, new Dictionary<HarnessId, DetectedAgent> { [HarnessId.Cursor] = new(false, true) });
            await unchosen.Vm.OnEnterAsync(CancellationToken.None);

            return (none.Vm.NoneFound, unchosen.Vm.DeclineVisible);
        });

        await Assert.That(noneFound).IsTrue();
        await Assert.That(declineVisible).IsTrue();
        await Assert.That(SetupLocation.NoneFoundTail).Contains(SetupLocation.Relaunch);
        await Assert.That(SetupLocation.NothingSelectedNote).Contains("Settings → Harnesses");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_harness_turned_down_before_starts_off_and_says_so() {
        var (record, tools, signal) = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness(Config.Root, declined: new HashSet<HarnessId> { HarnessId.Cursor });
            await h.Vm.OnEnterAsync(CancellationToken.None);

            return (h.Row("Cursor").Record, h.Row("Cursor").Tools, h.Row("Cursor").SignalLine);
        });

        await Assert.That(record).IsFalse();
        await Assert.That(tools).IsFalse();
        await Assert.That(signal).IsEqualTo("settings folder only · you turned this down before");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Detection_runs_once_across_repeated_entries() {
        var calls = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness(Config.Root);
            await h.Vm.OnEnterAsync(CancellationToken.None);
            await h.Vm.OnEnterAsync(CancellationToken.None);

            return h.DetectCalls;
        });

        await Assert.That(calls).IsEqualTo(1);
    }

    /// Claude Code installs its tools with capture, so the two answers cannot drift apart.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_bundled_harness_moves_tools_with_record() {
        var (bundled, tools) = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness(Config.Root);
            await h.Vm.OnEnterAsync(CancellationToken.None);
            h.Row("Claude Code").Record = false;

            return (h.Row("Claude Code").ToolsBundled, h.Row("Claude Code").Tools);
        });

        await Assert.That(bundled).IsTrue();
        await Assert.That(tools).IsFalse();
    }

    /// The first Next turns the rows on and stays, so each row's outcome is read on the page that ran it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Next_installs_each_selected_row_with_the_options_its_answer_needs() {
        var (flags, options, left, again, label, satisfied) = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness(Config.Root);
            await h.Vm.OnEnterAsync(CancellationToken.None);
            h.Row("Cursor").Tools = true; // tools only
            h.Row("Pi").Tools     = false; // capture only
            h.Vm.Visibility = "private";

            var leave = await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
            var again = await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);

            return (h.Cli.PluginInstallCalls.ToList(), h.Cli.PluginInstallOptions.ToList(), leave, again, h.Vm.NextLabel, h.Vm.Satisfied);
        });

        await Assert.That(flags).IsEquivalentTo([null, "--cursor", "--pi"], CollectionOrdering.Matching);
        await Assert.That(options[0]).IsEmpty();
        await Assert.That(options[1]).IsEquivalentTo(["--tools-only"]);
        await Assert.That(options[2]).IsEquivalentTo(["--skip-pi-mcp", "--skip-pi-skills", "--skip-pi-instructions"]);
        await Assert.That(left).IsFalse();
        await Assert.That(again).IsTrue();
        await Assert.That(label).IsEqualTo("Continue");
        await Assert.That(satisfied).IsTrue();
    }

    /// Turning harnesses on is confirmed on the page that did it: the wizard stays with the rows'
    /// outcomes, offers only Continue, and that Continue moves on without installing again.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_clean_install_stays_on_the_page_until_continue() {
        var (afterInstall, label, skipVisible, saved, afterContinue, calls) = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness(Config.Root);
            var vm = new OnboardingViewModel([h.Vm, new DoneStepViewModel(() => DoneFacts.Empty)]);
            await vm.PendingEnterForTesting;
            h.Vm.Visibility = "private";

            await vm.NextCommand.Execute().ToTask();
            var stayed = (vm.Current.Id, vm.NextLabel, vm.SkipVisible, h.Vm.ConnectionsSaved);
            await vm.NextCommand.Execute().ToTask();

            return (stayed.Id, stayed.NextLabel, stayed.SkipVisible, stayed.ConnectionsSaved, vm.Current.Id, h.Cli.PluginInstallCallCount);
        });

        await Assert.That(afterInstall).IsEqualTo(WizardStepId.Harnesses);
        await Assert.That(label).IsEqualTo("Continue");
        await Assert.That(skipVisible).IsFalse();
        await Assert.That(saved).IsTrue();
        await Assert.That(afterContinue).IsEqualTo(WizardStepId.Done);
        await Assert.That(calls).IsEqualTo(2); // Claude Code and Pi, once each
    }

    /// A failed row keeps the page with its own Retry; the next Next is the user carrying on.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_failed_install_holds_the_page_once_and_the_rest_still_install() {
        var (first, second, cursor, pi, label, calls) = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness(Config.Root);
            h.Cli.PluginInstallBehavior = (flag, _) => Task.FromResult(
                flag == "--cursor" ? new ProcessResult(1, "", "boom", false) : new ProcessResult(0, "", "", false));
            await h.Vm.OnEnterAsync(CancellationToken.None);
            h.Row("Cursor").Record = true;
            h.Row("Cursor").Tools  = true;
            h.Vm.Visibility = "private";
            var leave1 = await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
            await Assert.That(h.Vm.ChoicesEditable).IsFalse();
            await Assert.That(h.Vm.Recording).IsEquivalentTo([HarnessId.Claude, HarnessId.Pi]);
            var leave2 = await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);

            return (leave1, leave2, h.Row("Cursor"), h.Row("Pi"), h.Vm.NextLabel, h.Cli.PluginInstallCallCount);
        });

        await Assert.That(first).IsFalse();
        await Assert.That(second).IsTrue();
        await Assert.That(cursor.Failed).IsTrue();
        await Assert.That(cursor.Message).IsEqualTo("boom");
        await Assert.That(pi.Succeeded).IsTrue();
        await Assert.That(label).IsEqualTo("Continue");
        await Assert.That(calls).IsEqualTo(3); // the second Next installs nothing again
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Retry_reinstalls_only_its_own_row() {
        var (calls, satisfied) = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness(Config.Root);
            var fail = true;
            h.Cli.PluginInstallBehavior = (flag, _) => Task.FromResult(
                flag == "--cursor" && fail ? new ProcessResult(1, "", "boom", false) : new ProcessResult(0, "", "", false));
            await h.Vm.OnEnterAsync(CancellationToken.None);
            h.Row("Cursor").Record = true;
            h.Row("Cursor").Tools  = true;
            h.Vm.Visibility = "private";
            await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);

            fail = false;
            await h.Row("Cursor").RetryCommand.Execute().ToTask();
            Dispatcher.UIThread.RunJobs();

            return (h.Cli.PluginInstallCalls.ToList(), h.Vm.Satisfied);
        });

        await Assert.That(calls).IsEquivalentTo([null, "--cursor", "--pi", "--cursor"], CollectionOrdering.Matching);
        await Assert.That(satisfied).IsTrue();
    }

    static VisibilityOptionsResponse Offer(string preselected, params string[] keys) => new() {
        Options           = [.. keys.Select(k => new VisibilityOptionDto { Key = k, Label = $"label {k}", Detail = $"detail {k}" })],
        DefaultVisibility = preselected,
    };

    public static IEnumerable<Func<(Func<CancellationToken, Task<VisibilityOptionsResponse?>>? Read, string[] Values, string? FirstLabel)>> OfferRows() => [
        () => (_ => Task.FromResult<VisibilityOptionsResponse?>(Offer("private", "private", "public")), ["private", "public"], "label private"),
        () => (_ => Task.FromResult<VisibilityOptionsResponse?>(Offer("project", "private", "project", "public")), ["private", "project", "public"], "label private"),
        () => (_ => Task.FromResult<VisibilityOptionsResponse?>(Offer("org_public", "private", "project", "org_public", "public")),
               ["private", "project", "org_public", "public"], "label private"),
        () => (_ => Task.FromResult<VisibilityOptionsResponse?>(null), ["private", "project", "org_public", "public"], "Only me"),
        () => (_ => Task.FromException<VisibilityOptionsResponse?>(new HttpRequestException("down")), ["private", "project", "org_public", "public"], "Only me"),
        () => (null, ["private", "project", "org_public", "public"], "Only me"),
    ];

    /// The step offers the answers the server says this tenant can honour, in its order and copy, and every answer
    /// when the server says nothing or the read fails. It still waits for an explicit choice
    [Test]
    [MethodDataSource(nameof(OfferRows))]
    [NotInParallel("AvaloniaSession")]
    public async Task Who_can_read_offers_what_the_tenant_can_honour(
            Func<CancellationToken, Task<VisibilityOptionsResponse?>>? read, string[] values, string? firstLabel) {
        var (offered, label, visibility) = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness(Config.Root, visibilityOptions: read);
            await h.Vm.OnEnterAsync(CancellationToken.None);

            return (h.Vm.VisibilityOptions.Select(o => o.Value).ToList(), h.Vm.VisibilityOptions[0].Label, h.Vm.Visibility);
        });

        await Assert.That(offered).IsEquivalentTo(values, CollectionOrdering.Matching);
        await Assert.That(label).IsEqualTo(firstLabel);
        await Assert.That(visibility).IsNull();
    }

    /// A revisit re-reads the offer without detecting the harnesses again: an answer chosen from an earlier offer that
    /// the server no longer makes is cleared, so it cannot be saved.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_revisit_after_the_offer_shrank_clears_an_answer_no_longer_offered() {
        var (offered, visibility, detections) = await AvaloniaSession.DispatchAsync(async () => {
            var reads = 0;
            var h = new Harness(Config.Root,
                visibilityOptions: _ => Task.FromResult<VisibilityOptionsResponse?>(++reads == 1
                    ? Offer("org_public", "private", "project", "org_public", "public")
                    : Offer("private", "private", "public")));

            await h.Vm.OnEnterAsync(CancellationToken.None);
            h.Vm.Visibility = "org_public";
            await h.Vm.OnEnterAsync(CancellationToken.None);

            return (h.Vm.VisibilityOptions.Select(o => o.Value).ToList(), h.Vm.Visibility, h.DetectCalls);
        });

        await Assert.That(offered).IsEquivalentTo(["private", "public"], CollectionOrdering.Matching);
        await Assert.That(visibility).IsNull();
        await Assert.That(detections).IsEqualTo(1);
    }

    /// A revisit whose re-read fails falls back to every answer, as a first entry does, rather than keeping the
    /// earlier, narrower offer.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_revisit_whose_read_fails_offers_every_answer() {
        var (offered, visibility) = await AvaloniaSession.DispatchAsync(async () => {
            var reads = 0;
            var h = new Harness(Config.Root,
                visibilityOptions: _ => ++reads == 1
                    ? Task.FromResult<VisibilityOptionsResponse?>(Offer("private", "private", "public"))
                    : Task.FromException<VisibilityOptionsResponse?>(new HttpRequestException("down")));

            await h.Vm.OnEnterAsync(CancellationToken.None);
            h.Vm.Visibility = "public";
            await h.Vm.OnEnterAsync(CancellationToken.None);

            return (h.Vm.VisibilityOptions.Select(o => o.Value).ToList(), h.Vm.Visibility);
        });

        await Assert.That(offered).IsEquivalentTo(["private", "project", "org_public", "public"], CollectionOrdering.Matching);
        await Assert.That(visibility).IsEqualTo("public");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_local_install_asks_the_server_nothing() {
        var asked = await AvaloniaSession.DispatchAsync(async () => {
            var calls = 0;
            var h = new Harness(Config.Root, local: true, visibilityOptions: _ => { calls++; return Task.FromResult<VisibilityOptionsResponse?>(null); });
            await h.Vm.OnEnterAsync(CancellationToken.None);

            return calls;
        });

        await Assert.That(asked).IsEqualTo(0);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Next_saves_who_can_read_and_the_provider_key_answer_to_the_profile() {
        var existing = new ProfileConfig {
            ActiveProfile = "acme",
            Profiles      = new() { ["acme"] = new Profile { ServerUrl = "https://acme.example", DefaultVisibility = "project", UseProviderApiKey = true } },
        };
        await File.WriteAllTextAsync(ConfigPath, JsonSerializer.Serialize(existing, ProfileConfigJsonContext.Default.ProfileConfig));

        var (visibility, useKey) = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness(Config.Root) { ProviderKeys = new HashSet<string> { "ANTHROPIC_API_KEY" } };
            await h.Vm.OnEnterAsync(CancellationToken.None);
            await Assert.That(h.Vm.Visibility).IsNull();
            await Assert.That(h.Vm.CanContinue).IsFalse();
            await Assert.That(h.Vm.UseProviderApiKey).IsTrue();
            h.Vm.Visibility        = "private";
            h.Vm.UseProviderApiKey = true;

            await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);

            var profile = ConfigMutator.LoadPure(ConfigPath).Profiles["acme"];
            return (profile.DefaultVisibility, profile.UseProviderApiKey);
        });

        await Assert.That(visibility).IsEqualTo("private");
        await Assert.That(useKey).IsTrue();
    }

    /// The question is only asked when a key is exported and the harness that would use it records.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_provider_key_question_needs_the_key_and_its_harness() {
        var (withClaude, withoutKey, codexKeyOnly) = await AvaloniaSession.DispatchAsync(async () => {
            var a = new Harness(Config.Root) { ProviderKeys = new HashSet<string> { "ANTHROPIC_API_KEY" } };
            await a.Vm.OnEnterAsync(CancellationToken.None);
            var b = new Harness(Config.Root);
            await b.Vm.OnEnterAsync(CancellationToken.None);
            var c = new Harness(Config.Root) { ProviderKeys = new HashSet<string> { "OPENAI_API_KEY" } };
            await c.Vm.OnEnterAsync(CancellationToken.None);

            return (a.Vm.ProviderKeyNotice, b.Vm.ProviderKeyNotice, c.Vm.ProviderKeyNotice);
        });

        await Assert.That(withClaude).IsEqualTo("ANTHROPIC_API_KEY is set in your terminal.");
        await Assert.That(withoutKey).IsNull();
        await Assert.That(codexKeyOnly).IsNull(); // Codex was not detected
    }

    /// Hooks store the bare command, so Next puts kcap on the login shell before turning anything on.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Next_installs_the_terminal_command_then_turns_harnesses_on() {
        var (left, installed, label, calls) = await AvaloniaSession.DispatchAsync(async () => {
            var (h, fix) = await HarnessWithPathFix(failing: false);

            var leave = await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);

            return (leave, fix.Fixed, h.Vm.NextLabel, h.Cli.PluginInstallCallCount);
        });

        await Assert.That(left).IsFalse(); // the outcome stays on screen until Continue
        await Assert.That(installed).IsTrue();
        await Assert.That(label).IsEqualTo("Continue");
        await Assert.That(calls).IsEqualTo(2); // Cursor, found only by its settings folder, starts off
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_failed_terminal_install_stays_and_turns_nothing_on() {
        Skip.When(OperatingSystem.IsWindows(), "macOS lstat semantics");
        var (left, message, calls) = await AvaloniaSession.DispatchAsync(async () => {
            var (h, _) = await HarnessWithPathFix(failing: true);

            var leave = await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);

            return (leave, h.Vm.Message, h.Cli.PluginInstallCallCount);
        });

        await Assert.That(left).IsFalse();
        await Assert.That(message).StartsWith("Could not finish installing the terminal command.");
        await Assert.That(calls).IsEqualTo(0);
    }

    // A failing install meets someone else's kcap where it would link.
    async Task<(Harness, PathFixViewModel)> HarnessWithPathFix(bool failing) {
        var dir = Path.GetDirectoryName(ConfigPath)!;
        var link = Path.Combine(dir, ".local", "bin", "kcap");
        if (failing) {
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            File.WriteAllText(link, "someone else's");
        }
        var probe = new FakeLoginShellProbe {
            KcapOnPathBehavior = _ => Task.FromResult<bool?>(true),
            KcapPathBehavior = _ => Task.FromResult<string?>(link),
        };
        var fix = new PathFixViewModel(
            new PathShimInstaller(probe, new UserHome(dir)),
            new WizardFixtures.NoopAppStateStore(), Path.Combine(dir, "bundled-kcap"));
        var h = new Harness(Config.Root, pathFix: fix);
        await h.Vm.OnEnterAsync(CancellationToken.None);
        h.Vm.Visibility = "private";
        return (h, fix);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Not_now_still_leaves_while_the_terminal_cannot_run_kcap() {
        var (left, calls) = await AvaloniaSession.DispatchAsync(async () => {
            var fix = new PathFixViewModel(
                new PathShimInstaller(new FakeLoginShellProbe(), new UserHome(Path.GetDirectoryName(ConfigPath)!)),
                new WizardFixtures.NoopAppStateStore(), "/opt/kcap/kcap");
            var h = new Harness(Config.Root, pathFix: fix);
            await h.Vm.OnEnterAsync(CancellationToken.None);
            h.Vm.Visibility = "private";

            return (await h.Vm.CanLeaveAsync(WizardNavigation.Skip, CancellationToken.None), h.Cli.PluginInstallCallCount);
        });

        await Assert.That(left).IsTrue();
        await Assert.That(calls).IsEqualTo(0);
    }

    [Test]
    [Arguments(WizardNavigation.Next, true)]
    [Arguments(WizardNavigation.Skip, true)]
    [Arguments(WizardNavigation.Next, false)]
    [NotInParallel("AvaloniaSession")]
    public async Task Local_agents_continue_skip_and_none_detected_all_pass_keeping_the_stored_visibility(WizardNavigation direction, bool detectsAgents) {
        ConfigMutator.Mutate(Config.Root, c => c with { Profiles = new() { ["default"] = new Profile { DefaultVisibility = "project" } } });
        var (left, visible, saved) = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness(Config.Root, detectsAgents ? null : new Dictionary<HarnessId, DetectedAgent>(), local: true);
            await h.Vm.OnEnterAsync(CancellationToken.None);
            var canLeave = await h.Vm.CanLeaveAsync(direction, CancellationToken.None);
            // Turning detected agents on stays to show the outcome; Continue then leaves.
            if (direction == WizardNavigation.Next && detectsAgents) {
                await Assert.That(canLeave).IsFalse();
                await Assert.That(h.Vm.ConnectionsSaved).IsTrue();
                canLeave = await h.Vm.CanLeaveAsync(direction, CancellationToken.None);
            }

            return (canLeave && h.Vm.CanContinue && h.Vm.CanSkip, h.Vm.VisibilityVisible,
                ConfigMutator.LoadPure(ConfigPath).Profiles["default"].DefaultVisibility);
        });

        await Assert.That(left).IsTrue();
        await Assert.That(visible).IsFalse();
        await Assert.That(saved).IsEqualTo("project");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Local_harnesses_notes_name_no_terminal_command() {
        var (local, cloud) = await AvaloniaSession.DispatchAsync(async () => {
            var l = new Harness(Config.Root, local: true);
            var c = new Harness(Config.Root);
            await l.Vm.OnEnterAsync(CancellationToken.None);

            return (new[] { l.Vm.SavedNote, l.Vm.CliMissingNote, l.Vm.ProviderKeyDetail }, c.Vm.SavedNote);
        });

        foreach (var text in local) {
            await Assert.That(text).DoesNotContain("kcap plugin");
            await Assert.That(text).DoesNotContain("kcap config");
        }
        await Assert.That(cloud).IsEqualTo(SetupLocation.DisconnectLaterNote);
    }

    /// Not now installs nothing, so the PATH change is something the user can decline; once made, it
    /// reaches only terminals opened afterwards.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_terminal_notes_offer_not_now_and_send_the_user_to_a_new_terminal() {
        var (skip, before, after) = await AvaloniaSession.DispatchAsync(() => {
            var h = new Harness(Config.Root);

            return Task.FromResult((h.Vm.SkipLabel, string.Concat(h.Vm.PathNote.Select(s => s.Text)),
                string.Concat(h.Vm.PathResolvedNote.Select(s => s.Text))));
        });

        await Assert.That(before).Contains($"{skip} leaves your terminal as it is.");
        await Assert.That(after).StartsWith("Terminals you open from now on can run kcap");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Not_now_saves_privacy_without_installing_connections_and_records_the_offer() {
        var (calls, stamped) = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness(Config.Root);
            await h.Vm.OnEnterAsync(CancellationToken.None);
            h.Vm.Visibility = "private";
            await Assert.That(await h.Vm.CanLeaveAsync(WizardNavigation.Skip, CancellationToken.None)).IsTrue();

            return (h.Cli.PluginInstallCallCount, h.Stamped.ToList());
        });

        await Assert.That(calls).IsEqualTo(0);
        await Assert.That(stamped).IsEquivalentTo([HarnessId.Claude, HarnessId.Cursor, HarnessId.Pi]);
        await Assert.That(ConfigMutator.LoadPure(ConfigPath).Profiles["default"].DefaultVisibility).IsEqualTo("private");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_pane_renders_a_row_per_detected_harness_and_bundled_tools_as_text() {
        var (rows, toolBoxes) = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness(Config.Root);
            var vm = new OnboardingViewModel([h.Vm, new DoneStepViewModel(() => DoneFacts.Empty)]);
            await vm.PendingEnterForTesting;

            var window = new MainWindow { Onboarding = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var labels = window.GetVisualDescendants().OfType<TextBlock>().Count(t => t.Name == "HarnessLabelText");
            var tools  = window.GetVisualDescendants().OfType<CheckBox>().Count(c => c.Name == "ToolsCheckBox" && c.IsVisible);

            window.Close();
            Dispatcher.UIThread.RunJobs();

            return (labels, tools);
        });

        await Assert.That(rows).IsEqualTo(3);
        await Assert.That(toolBoxes).IsEqualTo(2); // Claude Code reads "bundled"
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel("AvaloniaSession")]
    public async Task Agent_controls_are_centered_under_headings_and_across_name_and_subtitle(bool succeeded) {
        await AvaloniaSession.RunOnUiAsync(async () => {
            var h = new Harness(Config.Root);
            var vm = new OnboardingViewModel([h.Vm, new DoneStepViewModel(() => DoneFacts.Empty)]);
            await vm.PendingEnterForTesting;
            // The check mark is laid out only when checked, and Cursor starts off.
            h.Row("Cursor").Record = true;
            h.Row("Cursor").Tools  = true;
            if (succeeded) h.Row("Claude Code").Status = AgentInstallStatus.Succeeded;
            var window = new MainWindow { Onboarding = vm, Width = 1024, Height = 1100 };
            window.Show();
            try {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(100);
                Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
                Dispatcher.UIThread.RunJobs();
                using var bitmap = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
                if (Environment.GetEnvironmentVariable("KCAP_ONBOARDING_PREVIEW") is { Length: > 0 } preview)
                    bitmap!.Save(preview, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                foreach (var check in window.GetVisualDescendants().OfType<CheckBox>()
                             .Where(c => c.IsVisible && c.Name is "RecordCheckBox" or "ToolsCheckBox")) {
                    var column = check.Name == "RecordCheckBox" ? "RECORD" : "TOOLS";
                    var heading = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == column);
                    var headingCenter = heading.TranslatePoint(new Point(heading.Bounds.Width / 2, 0), window)!.Value.X;
                    var square = check.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "NormalRectangle");
                    var center = square.TranslatePoint(new Point(square.Bounds.Width / 2, square.Bounds.Height / 2), window)!.Value;
                    var glyph = check.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single(p => p.Name == "CheckGlyph");
                    var glyphCenter = glyph.TranslatePoint(new Point(glyph.Bounds.Width / 2, glyph.Bounds.Height / 2), window)!.Value;
                    await Assert.That(Math.Abs(glyphCenter.X - center.X) <= 0.5).IsTrue();
                    await Assert.That(Math.Abs(glyphCenter.Y - center.Y) <= 0.5).IsTrue();
                    var row = check.GetVisualAncestors().OfType<Grid>().First();
                    var rowCenter = row.TranslatePoint(new Point(0, row.Bounds.Height / 2), window)!.Value.Y;
                    await Assert.That(Math.Abs(center.Y - rowCenter) <= 0.5).IsTrue();
                    await Assert.That(Math.Abs(center.X - headingCenter) <= 0.5).IsTrue();
                }
                foreach (var bundled in window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible && t.Text == "BUNDLED")) {
                    var row = bundled.GetVisualAncestors().OfType<Grid>().First();
                    var center = bundled.TranslatePoint(new Point(0, bundled.Bounds.Height / 2), row)!.Value.Y;
                    await Assert.That(Math.Abs(center - row.Bounds.Height / 2) <= 0.5).IsTrue();
                }
            } finally {
                window.Close();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Scrolling_hands_the_step_heading_to_the_title_bar_and_navigation_resets_it() =>
        AvaloniaSession.RunOnUiAsync(async () => {
            var h = new Harness(Config.Root);
            var vm = new OnboardingViewModel([h.Vm, new DoneStepViewModel(() => DoneFacts.Empty)]);
            await vm.PendingEnterForTesting;
            var window = new MainWindow { Onboarding = vm, Width = 1024, Height = 640 };
            window.Show();
            try {
                Dispatcher.UIThread.RunJobs();
                var scroll = window.GetVisualDescendants().OfType<ScrollViewer>().Single(s => s.Name == "StepScroll");
                var compact = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "CompactStepTitle");
                var heading = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "StepTitleText");
                await Assert.That(compact.Opacity).IsEqualTo(0);
                scroll.Offset = new Vector(0, 400);
                Dispatcher.UIThread.RunJobs();
                await Assert.That(compact.Opacity).IsEqualTo(1);
                await Assert.That(heading.Opacity).IsEqualTo(0);
                await Assert.That(compact.Text).IsEqualTo(vm.Current.Title);
                if (Environment.GetEnvironmentVariable("KCAP_ONBOARDING_PREVIEW") is { Length: > 0 } preview) {
                    await Task.Delay(100);
                    Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
                    using var bitmap = Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window);
                    bitmap!.Save(preview, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }
                scroll.Offset = default;
                Dispatcher.UIThread.RunJobs();
                await Assert.That(compact.Opacity).IsEqualTo(0);
                await Assert.That(heading.Opacity).IsEqualTo(1);
                scroll.Offset = new Vector(0, 400);
                Dispatcher.UIThread.RunJobs();
                h.Vm.Visibility = "private";
                await vm.NextCommand.Execute().ToTask();
                await vm.NextCommand.Execute().ToTask();
                await Assert.That(vm.Current.Id).IsEqualTo(WizardStepId.Done);
                Dispatcher.UIThread.RunJobs();
                await Assert.That(scroll.Offset.Y).IsEqualTo(0);
                await Assert.That(compact.Opacity).IsEqualTo(0);
                await Assert.That(compact.Text).IsEqualTo(vm.Current.Title);
            } finally {
                window.Close();
            }
        });

}
