using System.Reactive.Threading.Tasks;
using System.Runtime.Versioning;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.App.Views;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.App.Tests.Unit;

/// The PATH shim, the visibility/daemon-name defaults, and the closing
/// summary. Shim owns a ReactiveCommand (WhenAnyValue in its ctor), so it runs through the real
/// headless session like SignInStepViewModel; Defaults and Done own no commands and run directly,
/// like ConnectChoiceViewModel.
public class WizardSimpleStepsTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    // ── Shim: pure applicability decision ───────────────────────────────────

    [Test]
    [Arguments(false, "/opt/kcap/kcap", false, false)] // no installer for this OS
    [Arguments(true, null, false, false)]               // no resolved CLI
    [Arguments(true, "/opt/kcap/kcap", true, false)]     // already on PATH
    [Arguments(true, "/opt/kcap/kcap", null, false)]     // probe inconclusive — fail quiet
    [Arguments(true, "/opt/kcap/kcap", false, true)]     // installer + CLI + positively absent
    public async Task ComputeApplicable_matches_the_spec_decision(bool hasInstaller, string? target, bool? onPath, bool expected) {
        await Assert.That(PathFixViewModel.ComputeApplicable(hasInstaller, target, onPath)).IsEqualTo(expected);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Windows_path_fix_uses_user_path_and_reports_whether_it_was_verified(bool verified) {
        using var tmp = new TempDir();
        var target = tmp.PathTo("kcap.exe");
        var paths = new MemoryUserPath();
        var probe = new FakeLoginShellProbe { KcapOnPathBehavior = _ => Task.FromResult<bool?>(verified ? true : null) };
        var store = new FakeAppStateStore();
        var windows = new WindowsUserPathInstaller(paths, probe);

        var (fixedPath, disclosure, message) = await AvaloniaSession.DispatchAsync(async () => {
            var vm = new PathFixViewModel(windows, store, target);
            await vm.InstallCommand.Execute().ToTask();
            Dispatcher.UIThread.RunJobs();
            return (vm.Fixed, vm.Disclosure, vm.Message);
        });

        await Assert.That(fixedPath).IsEqualTo(verified);
        await Assert.That(paths.Read()).IsEqualTo(Path.GetDirectoryName(target));
        await Assert.That(disclosure).Contains("No administrator rights are needed");
        await Assert.That(store.Updates).IsEqualTo(1);
        if (!verified) {
            await Assert.That(message).Contains("was added to your user PATH");
            await Assert.That(message).DoesNotContain("nothing changed");
        }
    }

    sealed class MemoryUserPath : IUserPathStore {
        string? _value;
        public string? Read() => _value;
        public void Append(string directory) => _value = directory;
    }

    // ── Shim: install / claim / outcome mapping ─────────────────────────────

    sealed class FakeProcessRunner : IProcessRunner {
        Func<Task<ProcessResult>> _step = () => Task.FromResult(new ProcessResult(0, "", "", false));

        public void Enqueue(ProcessResult result) => _step = () => Task.FromResult(result);

        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) => _step();

        public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options,
            Action<StreamedLine> onLine, CancellationToken ct) => throw new NotImplementedException();
    }

    sealed class FakeAppStateStore : IAppStateStore {
        public AppState State = new();
        public int Updates;

        public Task<AppState> LoadAsync() => Task.FromResult(State);

        public Task<bool> UpdateAsync(Func<AppState, AppState> mutate) {
            Updates++;
            State = mutate(State);

            return Task.FromResult(true);
        }
    }

    sealed class ShimHarness : IDisposable {
        readonly TempDir _tmp = new();
        public string TempDir => _tmp.Path;
        public readonly FakeProcessRunner  Runner = new();
        public readonly FakeLoginShellProbe Probe = new();
        public readonly FakeAppStateStore  Store  = new();
        public readonly PathShimInstaller  Installer;
        public readonly string             Destination;
        public readonly string             Target;
        public readonly PathFixViewModel   Vm;

        public ShimHarness() {
            Destination = Path.Combine(TempDir, "kcap");
            Target      = Path.Combine(TempDir, "target-cli");
            Installer   = new PathShimInstaller(Runner, Probe, Destination);
            Vm          = new PathFixViewModel(Installer, Store, Target);
        }

        // InstallCommand's IsExecuting/CanExecute/End notifications ride the dispatcher scheduler;
        // drain them on the session thread before returning so this shared-session test leaves no
        // dispatcher-queued work a sibling's frame could later surface off the UI thread.
        public async Task Install() {
            await Vm.InstallCommand.Execute().ToTask();
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose() => _tmp.Dispose();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Install_claims_ShimOffered_exactly_once_across_two_clicks() {
        var updates = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new ShimHarness();
            h.Runner.Enqueue(new ProcessResult(0, "", "", false));
            h.Probe.KcapOnPathBehavior = _ => Task.FromResult<bool?>(true);

            await h.Install();
            await h.Install();

            return h.Store.Updates;
        });

        await Assert.That(updates).IsEqualTo(1);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Never_installing_never_claims() {
        var updates = await AvaloniaSession.DispatchAsync(() => {
            using var h = new ShimHarness();

            return h.Store.Updates;
        });

        await Assert.That(updates).IsEqualTo(0);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Installed_outcome_fixes_the_path_with_no_message() {
        var (satisfied, message) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new ShimHarness();
            h.Runner.Enqueue(new ProcessResult(0, "", "", false));
            h.Probe.KcapOnPathBehavior = _ => Task.FromResult<bool?>(true);

            await h.Install();

            return (h.Vm.Fixed, h.Vm.Message);
        });

        await Assert.That(satisfied).IsTrue();
        await Assert.That(message).IsNull();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task InstalledButNotOnPath_outcome_is_not_fixed_and_carries_the_installer_detail() {
        var (satisfied, message) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new ShimHarness();
            h.Runner.Enqueue(new ProcessResult(0, "", "", false));
            h.Probe.KcapOnPathBehavior = _ => Task.FromResult<bool?>(false);

            await h.Install();

            return (h.Vm.Fixed, h.Vm.Message);
        });

        await Assert.That(satisfied).IsFalse();
        await Assert.That(message).IsNotNull();
        await Assert.That(message).Contains("PATH");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Cancelled_outcome_is_not_fixed_and_says_nothing_changed() {
        var (satisfied, message) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new ShimHarness();
            h.Runner.Enqueue(new ProcessResult(1, "", "User canceled. (-128)", false));

            await h.Install();

            return (h.Vm.Fixed, h.Vm.Message);
        });

        await Assert.That(satisfied).IsFalse();
        await Assert.That(message).IsEqualTo("Cancelled. Nothing changed.");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Failed_outcome_is_not_fixed_and_names_the_sudo_fallback() {
        var (satisfied, message) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new ShimHarness();
            h.Runner.Enqueue(new ProcessResult(1, "", "Permission denied", false));

            await h.Install();

            return (h.Vm.Fixed, h.Vm.Message);
        });

        await Assert.That(satisfied).IsFalse();
        await Assert.That(message).IsNotNull();
        await Assert.That(message).StartsWith("Could not finish installing the terminal command.");
        await Assert.That(message).Contains("sudo mkdir -p /usr/local/bin");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Null_target_reports_kcap_not_found_and_claims_nothing() {
        var (satisfied, message, updates) = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new ShimHarness();
            var vm = new PathFixViewModel(h.Installer, h.Store, null);

            await vm.InstallCommand.Execute().ToTask();
            Dispatcher.UIThread.RunJobs(); // drain the command's dispatcher-scheduled notifications, as ShimHarness.Install does

            return (vm.Fixed, vm.Message, h.Store.Updates);
        });

        await Assert.That(satisfied).IsFalse();
        await Assert.That(message).IsEqualTo("This machine could not find its own kcap, so nothing changed.");
        await Assert.That(updates).IsEqualTo(0);
    }

    // ── templates ────────────────────────────────────────────────────────────

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_window_renders_the_done_page_from_its_facts() {
        var result = await AvaloniaSession.DispatchAsync(async () => {
            var done = new DoneStepViewModel(() => new DoneFacts(["Claude Code"], false, false, null, true, "test-mac", null));
            var vm = new OnboardingViewModel([done]);
            await vm.PendingEnterForTesting;

            var window = new MainWindow { Onboarding = vm };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var texts    = window.GetVisualDescendants().OfType<TextBlock>().ToList();
            var title    = texts.FirstOrDefault(t => t.Name == "StepTitleText")?.Text;
            var daemon   = texts.FirstOrDefault(t => t.Name == "DaemonLineText")?.Text;
            var capture  = window.GetVisualDescendants().OfType<ItemsControl>().FirstOrDefault(i => i.Name == "CaptureItems")?.ItemCount;

            window.Close();
            Dispatcher.UIThread.RunJobs();

            return (title, daemon, capture);
        });

        await Assert.That(result.title).IsEqualTo("From now on, your agents remember");
        await Assert.That(result.daemon).StartsWith("Running as a service on test-mac.");
        await Assert.That(result.capture).IsEqualTo(1);
    }
}

/// Real ConfigMutator against the config path.
public class MachineNameViewModelTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    string ConfigPath => AppConfig.GetConfigPath(Config.Root);

    [Test]
    public async Task The_default_name_is_the_lowercased_username() {
        var vm = new MachineNameViewModel(Config.Root);

        await Assert.That(vm.DaemonName).IsEqualTo(Environment.UserName.ToLowerInvariant());
        await Assert.That(vm.Saved).IsFalse();
    }

    [Test]
    public async Task Save_persists_the_name_and_preserves_unrelated_config() {
        var existing = new ProfileConfig {
            ActiveProfile = "acme",
            Profiles = new() {
                ["acme"] = new Profile {
                    ServerUrl     = "https://acme.example",
                    ExcludedRepos = ["foo/bar"],
                    ImportOrg     = "acme-org",
                    Daemon        = new DaemonSettings { MaxAgents = 9, ClaudePath = "/usr/bin/claude" },
                }
            },
            MachineId = "machine-123",
        };
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(existing, ProfileConfigJsonContext.Default.ProfileConfig));

        var vm = new MachineNameViewModel(Config.Root) { DaemonName = "acme-daemon" };

        var canLeave = await vm.SaveAsync(CancellationToken.None);

        await Assert.That(canLeave).IsTrue();
        await Assert.That(vm.Saved).IsTrue();

        var saved   = ConfigMutator.LoadPure(ConfigPath);
        var profile = saved.Profiles["acme"];

        await Assert.That(profile.DefaultVisibility).IsEqualTo("org_public");
        await Assert.That(profile.Daemon!.Name).IsEqualTo("acme-daemon");
        await Assert.That(profile.Daemon!.MaxAgents).IsEqualTo(9);
        await Assert.That(profile.Daemon!.ClaudePath).IsEqualTo("/usr/bin/claude");
        await Assert.That(profile.ServerUrl).IsEqualTo("https://acme.example");
        await Assert.That(profile.ImportOrg).IsEqualTo("acme-org");
        await Assert.That(profile.ExcludedRepos).IsEquivalentTo(["foo/bar"]);
        await Assert.That(saved.MachineId).IsEqualTo("machine-123");
    }

    // The persist must follow the wizard's resolved identity, not on-disk ActiveProfile (KCAP_PROFILE split).
    [Test]
    public async Task Save_persists_to_the_injected_resolved_profile_not_the_active_one() {
        var existing = new ProfileConfig {
            ActiveProfile = "acme",
            Profiles = new() {
                ["acme"] = new Profile { ServerUrl = "https://acme.example" },
                ["work"] = new Profile { ServerUrl = "https://work.example" },
            },
        };
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(existing, ProfileConfigJsonContext.Default.ProfileConfig));

        var vm = new MachineNameViewModel(Config.Root, resolveProfileName: () => "work") { DaemonName = "work-daemon" };

        var canLeave = await vm.SaveAsync(CancellationToken.None);

        await Assert.That(canLeave).IsTrue();
        await Assert.That(vm.Saved).IsTrue();

        var saved = ConfigMutator.LoadPure(ConfigPath);

        await Assert.That(saved.Profiles["work"].Daemon!.Name).IsEqualTo("work-daemon");
        // The active profile (acme) is untouched — the mutation targeted the RESOLVED name.
        await Assert.That(saved.Profiles["acme"].Daemon).IsNull();
    }

    [Test]
    public async Task A_resolved_name_absent_from_config_falls_back_to_the_active_profile() {
        var existing = new ProfileConfig {
            ActiveProfile = "acme",
            Profiles = new() { ["acme"] = new Profile { ServerUrl = "https://acme.example" } },
        };
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(existing, ProfileConfigJsonContext.Default.ProfileConfig));

        var vm = new MachineNameViewModel(Config.Root, resolveProfileName: () => "ghost") { DaemonName = "acme-daemon" };

        await vm.SaveAsync(CancellationToken.None);

        var saved = ConfigMutator.LoadPure(ConfigPath);

        await Assert.That(saved.Profiles["acme"].Daemon!.Name).IsEqualTo("acme-daemon");
        await Assert.That(saved.Profiles.ContainsKey("ghost")).IsFalse();
    }

    [Test]
    public async Task A_null_resolved_name_falls_back_to_the_active_profile() {
        var existing = new ProfileConfig {
            ActiveProfile = "acme",
            Profiles = new() { ["acme"] = new Profile { ServerUrl = "https://acme.example" } },
        };
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(existing, ProfileConfigJsonContext.Default.ProfileConfig));

        var vm = new MachineNameViewModel(Config.Root, resolveProfileName: () => null) { DaemonName = "acme-daemon" };

        await vm.SaveAsync(CancellationToken.None);

        var saved = ConfigMutator.LoadPure(ConfigPath);

        await Assert.That(saved.Profiles["acme"].Daemon!.Name).IsEqualTo("acme-daemon");
    }

    // A real write failure (read-only config dir), not a fake, proves CanLeaveAsync's own catch.
    [Test]
    [UnsupportedOSPlatform("windows")]
    public async Task A_save_failure_is_refused_with_a_visible_message() {
        Skip.When(OperatingSystem.IsWindows(), "chmod-based read-only config dir is POSIX-only.");

        var dir = Path.GetDirectoryName(ConfigPath)!;
        var vm  = new MachineNameViewModel(Config.Root) { DaemonName = "acme-daemon" };

        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try {
            var canLeave = await vm.SaveAsync(CancellationToken.None);

            await Assert.That(canLeave).IsFalse();
            await Assert.That(vm.Saved).IsFalse();
            await Assert.That(vm.Message).IsNotNull();
            await Assert.That(vm.Message).Contains("Could not save the machine name");
        } finally {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}

/// The last page reads its headline and figures off what the earlier pages did.
public class DoneStepViewModelTests {
    static DoneFacts Facts(IReadOnlyList<string>? recording = null, bool hazard = false, HistoryImportRun? import = null) =>
        new(recording ?? ["Claude Code"], false, hazard, import, true, "test-mac", "https://acme.kcap.ai");

    static async Task<HistoryImportRun> FinishedRun(int imported, int failed) {
        var cli = new FakeKcapCli {
            ImportBehavior = (_, onLine, _) => {
                onLine(new StreamedLine(ProcessStreamKind.Stdout, $"  {imported} imported · 0 skipped · {failed} failed"));
                return Task.FromResult(new StreamingResult(0, false, []));
            },
        };
        var run = new HistoryImportRun(100, 2, "from the last 90 days", 1, action => action());
        run.Start(cli, [new ImportRequest(ImportScopeChoice.Repo, null, [], ["a/b"])]);
        await run.Completion;

        return run;
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_headline_follows_what_actually_happened() {
        var (imported, remembering, nothing, blocked) = await AvaloniaSession.DispatchAsync(async () => {
            async Task<string> TitleFor(DoneFacts facts) {
                var vm = new DoneStepViewModel(() => facts);
                await vm.OnEnterAsync(CancellationToken.None);
                return vm.Title;
            }

            return (await TitleFor(Facts(import: await FinishedRun(40, 0))), await TitleFor(Facts()),
                await TitleFor(Facts(recording: [])), await TitleFor(Facts(hazard: true)));
        });

        await Assert.That(imported).IsEqualTo("Your work so far");
        await Assert.That(remembering).IsEqualTo("From now on, your agents remember");
        await Assert.That(nothing).IsEqualTo("No new recording connections");
        await Assert.That(blocked).IsEqualTo("No new recording connections");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Failed_uploads_are_named_with_where_they_still_are() {
        var (visible, title, body, landed, unit) = await AvaloniaSession.DispatchAsync(async () => {
            var vm = new DoneStepViewModel(() => Facts(import: null));
            var run = await FinishedRun(30, 3);
            vm = new DoneStepViewModel(() => Facts(import: run));
            await vm.OnEnterAsync(CancellationToken.None);

            return (vm.FailedVisible, vm.FailedTitle, vm.FailedBody, vm.LandedValue, vm.LandedUnit);
        });

        await Assert.That(visible).IsTrue();
        await Assert.That(title).IsEqualTo("3 sessions failed to upload");
        await Assert.That(body).Contains("test-mac");
        await Assert.That(body).Contains("retry");
        await Assert.That(landed).IsEqualTo("30");
        await Assert.That(unit).IsEqualTo("/ 100");
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_facts_are_read_again_on_every_entry() {
        var calls = await AvaloniaSession.DispatchAsync(async () => {
            var count = 0;
            var vm = new DoneStepViewModel(() => { count++; return Facts(); });
            await vm.OnEnterAsync(CancellationToken.None);
            await vm.OnEnterAsync(CancellationToken.None);
            return count;
        });

        await Assert.That(calls).IsEqualTo(2);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Open_your_workspace_opens_the_signed_in_server() {
        var opened = await AvaloniaSession.DispatchAsync(async () => {
            var opener = new RecordingUrlOpener();
            var vm = new DoneStepViewModel(() => Facts(), opener);
            await vm.OnEnterAsync(CancellationToken.None);
            vm.OpenWorkspaceCommand.Execute().Subscribe();
            Dispatcher.UIThread.RunJobs();
            return opener.Opened.ToList();
        });

        await Assert.That(opened).IsEquivalentTo(["https://acme.kcap.ai"]);
    }

    sealed class RecordingUrlOpener : IUrlOpener {
        public readonly List<string> Opened = [];
        public void Open(string url) => Opened.Add(url);
    }
}
