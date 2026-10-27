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
            await vm.InstallAsync();
            return (vm.Fixed, string.Concat(vm.Plan.Select(s => s.Text)), vm.Message);
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
        public void Replace(string path) => _value = path;
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

    sealed class ScriptedInstaller(ShimResult result) : ICliPathInstaller {
        public string Disclosure => "";
        public ShimPreflight Preflight(string target) => throw new NotSupportedException();
        public Task<ShimResult> InstallAsync(string target, CancellationToken ct) => Task.FromResult(result);
    }

    sealed class GatedInstaller : ICliPathInstaller {
        public readonly TaskCompletionSource<ShimResult> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public string Disclosure => "";
        public ShimPreflight Preflight(string target) => throw new NotSupportedException();
        public Task<ShimResult> InstallAsync(string target, CancellationToken ct) {
            Calls++;
            return Result.Task;
        }
    }

    /// The local install starts the link in the background; the harness step's Next joins it
    /// rather than raising a second admin prompt.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_second_install_while_one_runs_joins_it() {
        var (calls, first, second) = await AvaloniaSession.DispatchAsync(async () => {
            var installer = new GatedInstaller();
            var vm = new PathFixViewModel(installer, new FakeAppStateStore(), "/x");

            var background = vm.InstallAsync();
            var next       = vm.InstallAsync();
            installer.Result.SetResult(new ShimResult(ShimOutcome.Installed, null, null));

            return (installer.Calls, await background, await next);
        });

        await Assert.That(calls).IsEqualTo(1);
        await Assert.That(first).IsTrue();
        await Assert.That(second).IsTrue();
    }

    static readonly ShimResult[] UnfinishedShims = [
        new(ShimOutcome.InstalledButNotOnPath, null, null),
        new(ShimOutcome.InstalledButNotOnPath, "Add: export PATH=\"/usr/local/bin:$PATH\"", null),
        new(ShimOutcome.Failed, "Permission denied.", "sudo ln -s /x /usr/local/bin/kcap"),
    ];

    static async Task<List<string?>> PathFixMessagesAsync(bool onLocalPath) =>
        await AvaloniaSession.DispatchAsync(async () => {
            var messages = new List<string?>();
            foreach (var result in UnfinishedShims) {
                var vm = new PathFixViewModel(new ScriptedInstaller(result), new FakeAppStateStore(), "/x", () => onLocalPath);
                await vm.InstallAsync();
                messages.Add(vm.Message);
            }

            return messages;
        });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_path_fix_on_the_local_path_names_no_terminal_command() {
        var messages = await PathFixMessagesAsync(onLocalPath: true);

        foreach (var message in messages) {
            await Assert.That(message).IsNotNull();
            await Assert.That(message!).DoesNotContain("kcap ");
            await Assert.That(message).DoesNotContain("sudo");
            await Assert.That(message).DoesNotContain("export PATH");
        }
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_path_fix_on_the_cloud_path_keeps_its_terminal_recovery() {
        var messages = await PathFixMessagesAsync(onLocalPath: false);

        await Assert.That(messages[0]).Contains("run kcap --version");
        await Assert.That(messages[1]).Contains("export PATH");
        await Assert.That(messages[2]).Contains("sudo ln -s");
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
            Probe.KcapPathBehavior = _ => Task.FromResult<string?>(Destination);
            Installer   = new PathShimInstaller(Runner, Probe, Destination);
            Vm          = new PathFixViewModel(Installer, Store, Target);
        }

        public Task Install() => Vm.InstallAsync();

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

    /// A background link's failure is shown to nobody, so the app's startup offer must stay free to raise it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_background_link_claims_no_offer() {
        var updates = await AvaloniaSession.DispatchAsync(async () => {
            using var h = new ShimHarness();
            h.Runner.Enqueue(new ProcessResult(1, "", "Permission denied", false));

            await h.Vm.LinkAsync(CancellationToken.None);

            return h.Store.Updates;
        });

        await Assert.That(updates).IsEqualTo(0);
    }

    /// The handoff carries a failure the user may not have seen, but not their own cancel.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_unfinished_link_hands_off_its_reason_but_not_a_cancel() {
        var (failed, cancelled, none) = await AvaloniaSession.DispatchAsync(async () => {
            var failing = new PathFixViewModel(
                new ScriptedInstaller(new ShimResult(ShimOutcome.Failed, "Permission denied.", null)), new FakeAppStateStore(), "/x");
            var cancelling = new PathFixViewModel(
                new ScriptedInstaller(new ShimResult(ShimOutcome.Cancelled, null, null)), new FakeAppStateStore(), "/x");
            var idle = new PathFixViewModel(
                new ScriptedInstaller(new ShimResult(ShimOutcome.Failed, null, null)), new FakeAppStateStore(), "/x");
            await failing.LinkAsync(CancellationToken.None);
            await cancelling.LinkAsync(CancellationToken.None);

            return (await failing.UnfinishedAsync(), await cancelling.UnfinishedAsync(), await idle.UnfinishedAsync());
        });

        await Assert.That(failed).Contains("Permission denied.");
        await Assert.That(cancelled).IsNull();
        await Assert.That(none).IsNull();
    }

    sealed class SequencedInstaller(params Func<ShimResult>[] attempts) : ICliPathInstaller {
        int _next;
        public string Disclosure => "";
        public ShimPreflight Preflight(string target) => throw new NotSupportedException();
        public Task<ShimResult> InstallAsync(string target, CancellationToken ct) => Task.FromResult(attempts[_next++]());
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_cancel_does_not_hide_a_later_attempts_failure() {
        var unfinished = await AvaloniaSession.DispatchAsync(async () => {
            var vm = new PathFixViewModel(
                new SequencedInstaller(
                    () => new ShimResult(ShimOutcome.Cancelled, null, null),
                    () => throw new IOException("disk full")),
                new FakeAppStateStore(), "/x");
            await vm.LinkAsync(CancellationToken.None);
            await vm.LinkAsync(CancellationToken.None);

            return await vm.UnfinishedAsync();
        });

        await Assert.That(unfinished).Contains("disk full");
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

            await vm.InstallAsync();

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
            var daemon   = texts.FirstOrDefault(t => t.Name == "ServiceStatusText")?.Text;
            var capture  = window.GetVisualDescendants().OfType<ItemsControl>().FirstOrDefault(i => i.Name == "CaptureItems")?.ItemCount;

            window.Close();
            Dispatcher.UIThread.RunJobs();

            return (title, daemon, capture);
        });

        await Assert.That(result.title).IsEqualTo("From now on, your agents remember");
        await Assert.That(result.daemon).IsEqualTo("Running");
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
