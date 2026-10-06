using System.Reactive.Threading.Tasks;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.App.Views;
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
                IReadOnlySet<HarnessId>? declined = null, PathFixViewModel? pathFix = null) {
            detected ??= new Dictionary<HarnessId, DetectedAgent> {
                [HarnessId.Claude] = new(true, true),
                [HarnessId.Cursor] = new(false, true),
                [HarnessId.Pi]     = new(true, false),
            };
            Vm = new HarnessesStepViewModel(
                Cli,
                _ => { DetectCalls++; return Task.FromResult(detected); },
                () => declined ?? new HashSet<HarnessId>(),
                Stamped.AddRange,
                config, pathFix,
                _ => Task.FromResult<IReadOnlySet<string>?>(ProviderKeys),
                "test-mac");
        }

        public HarnessRowViewModel Row(string label) => Vm.Rows.First(r => r.Label == label);
    }

    string ConfigPath => AppConfig.GetConfigPath(Config.Root);

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
        await Assert.That(signals).IsEquivalentTo(["on your PATH", "config found", "on your PATH"], CollectionOrdering.Matching);
        await Assert.That(answers.All(a => a is (true, true))).IsTrue();
        await Assert.That(notFound).StartsWith("Not found: Codex, Copilot");
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
        await Assert.That(signal).IsEqualTo("config found · you turned this down before");
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

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Next_installs_each_selected_row_with_the_options_its_answer_needs() {
        var (flags, options, left, again, label, satisfied) = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness(Config.Root);
            await h.Vm.OnEnterAsync(CancellationToken.None);
            h.Row("Cursor").Record = false; // tools only
            h.Row("Pi").Tools      = false; // capture only

            var leave = await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
            var again = await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);

            return (h.Cli.PluginInstallCalls.ToList(), h.Cli.PluginInstallOptions.ToList(), leave, again, h.Vm.NextLabel, h.Vm.Satisfied);
        });

        await Assert.That(flags).IsEquivalentTo([null, "--cursor", "--pi"], CollectionOrdering.Matching);
        await Assert.That(options[0]).IsEmpty();
        await Assert.That(options[1]).IsEquivalentTo(["--tools-only"]);
        await Assert.That(options[2]).IsEquivalentTo(["--skip-pi-mcp", "--skip-pi-skills", "--skip-pi-instructions"]);
        await Assert.That(left).IsTrue();
        await Assert.That(again).IsTrue();
        await Assert.That(label).IsEqualTo("Continue");
        await Assert.That(satisfied).IsTrue();
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

            var leave1 = await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
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
            await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);

            fail = false;
            await h.Row("Cursor").RetryCommand.Execute().ToTask();
            Dispatcher.UIThread.RunJobs();

            return (h.Cli.PluginInstallCalls.ToList(), h.Vm.Satisfied);
        });

        await Assert.That(calls).IsEquivalentTo([null, "--cursor", "--pi", "--cursor"], CollectionOrdering.Matching);
        await Assert.That(satisfied).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Next_saves_who_can_read_and_the_provider_key_answer_to_the_profile() {
        var existing = new ProfileConfig {
            ActiveProfile = "acme",
            Profiles      = new() { ["acme"] = new Profile { ServerUrl = "https://acme.example" } },
        };
        await File.WriteAllTextAsync(ConfigPath, JsonSerializer.Serialize(existing, ProfileConfigJsonContext.Default.ProfileConfig));

        var (visibility, useKey) = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness(Config.Root) { ProviderKeys = new HashSet<string> { "ANTHROPIC_API_KEY" } };
            await h.Vm.OnEnterAsync(CancellationToken.None);
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

    /// Hooks store the bare command, so a selected harness cannot leave until the login shell finds it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Next_waits_for_the_terminal_command_and_installs_nothing_until_it_resolves() {
        var (held, callsWhileHeld, message, left, again, label, callsAfter) = await AvaloniaSession.DispatchAsync(async () => {
            var dir = Path.GetDirectoryName(ConfigPath)!;
            var destination = Path.Combine(dir, "shim-kcap");
            var probe = new FakeLoginShellProbe { KcapOnPathBehavior = _ => Task.FromResult<bool?>(true) };
            var runner = new SucceedingProcessRunner();
            var fix = new PathFixViewModel(
                new PathShimInstaller(runner, probe), new WizardFixtures.NoopAppStateStore(),
                Path.Combine(dir, "bundled-kcap"), destination);
            var h = new Harness(Config.Root, pathFix: fix);
            await h.Vm.OnEnterAsync(CancellationToken.None);

            var heldLeave = await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
            var heldCalls = h.Cli.PluginInstallCallCount;
            var heldMessage = h.Vm.Message;

            await fix.InstallCommand.Execute().ToTask();
            Dispatcher.UIThread.RunJobs();
            var leave = await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);
            var again = await h.Vm.CanLeaveAsync(WizardNavigation.Next, CancellationToken.None);

            return (heldLeave, heldCalls, heldMessage, leave, again, h.Vm.NextLabel, h.Cli.PluginInstallCallCount);
        });

        await Assert.That(held).IsFalse();
        await Assert.That(callsWhileHeld).IsEqualTo(0);
        await Assert.That(message).IsEqualTo(HarnessesStepViewModel.PathRequiredMessage);
        await Assert.That(left).IsTrue();
        await Assert.That(again).IsTrue();
        await Assert.That(label).IsEqualTo("Continue");
        await Assert.That(callsAfter).IsEqualTo(3);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Not_now_still_leaves_while_the_terminal_cannot_run_kcap() {
        var (left, calls) = await AvaloniaSession.DispatchAsync(async () => {
            var fix = new PathFixViewModel(
                new PathShimInstaller(new WizardFixtures.NoopProcessRunner(), new FakeLoginShellProbe()),
                new WizardFixtures.NoopAppStateStore(), "/opt/kcap/kcap");
            var h = new Harness(Config.Root, pathFix: fix);
            await h.Vm.OnEnterAsync(CancellationToken.None);

            return (await h.Vm.CanLeaveAsync(WizardNavigation.Skip, CancellationToken.None), h.Cli.PluginInstallCallCount);
        });

        await Assert.That(left).IsTrue();
        await Assert.That(calls).IsEqualTo(0);
    }

    sealed class SucceedingProcessRunner : IProcessRunner {
        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) =>
            Task.FromResult(new ProcessResult(0, "", "", false));

        public Task<StreamingResult> RunStreamingAsync(
                string fileName, string[] args, RunOptions options, Action<StreamedLine> onLine, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Not_now_installs_nothing_and_writes_nothing_but_records_the_offer() {
        var (calls, stamped) = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness(Config.Root);
            await h.Vm.OnEnterAsync(CancellationToken.None);

            await h.Vm.CanLeaveAsync(WizardNavigation.Skip, CancellationToken.None);

            return (h.Cli.PluginInstallCallCount, h.Stamped.ToList());
        });

        await Assert.That(calls).IsEqualTo(0);
        await Assert.That(stamped).IsEquivalentTo([HarnessId.Claude, HarnessId.Cursor, HarnessId.Pi]);
        await Assert.That(File.Exists(ConfigPath)).IsFalse();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_primary_label_counts_the_selected_harnesses() {
        var (three, one, none) = await AvaloniaSession.DispatchAsync(async () => {
            var h = new Harness(Config.Root);
            await h.Vm.OnEnterAsync(CancellationToken.None);
            var all = h.Vm.NextLabel;
            h.Row("Claude Code").Record = false;
            h.Row("Cursor").Record = false;
            h.Row("Cursor").Tools = false;
            var single = h.Vm.NextLabel;
            h.Row("Pi").Record = false;
            h.Row("Pi").Tools = false;

            return (all, single, h.Vm.NextLabel);
        });

        await Assert.That(three).IsEqualTo("Turn on for 3 harnesses");
        await Assert.That(one).IsEqualTo("Turn on for 1 harness");
        await Assert.That(none).IsEqualTo("Turn on");
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
}
