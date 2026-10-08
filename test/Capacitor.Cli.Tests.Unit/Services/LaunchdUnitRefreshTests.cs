using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>
/// A job installed as Adaptive gets its plist rewritten to Standard, but is reloaded only when the
/// daemon accepts an idle-only restart, because the reload kills whatever it hosts.
/// </summary>
public class LaunchdUnitRefreshTests {
    [TempHome] public required TempHome Home { get; init; }

    const string Label = "io.kurrent.kcap.daemon.test";

    static ServiceSpec Spec() => new(
        ServiceId: "test",
        DaemonBinaryPath: "/opt/kcap/kcap-daemon",
        LogPath: "/home/u/.config/kcap/daemon-test.log",
        Environment: new Dictionary<string, string>(),
        ExtraArgs: []);

    static string AdaptivePlist() =>
        LaunchdUnit.Plist(Spec()).Replace("<string>Standard</string>", "<string>Adaptive</string>");

    string Seed(string content) {
        Directory.CreateDirectory(LaunchdUnit.AgentsDir(Home));
        var path = LaunchdUnit.PlistPath(Home, "test");
        File.WriteAllText(path, content);
        return path;
    }

    static string Print(string spawnType, bool running = true) =>
        $"gui/501/{Label} = {{\n\tstate = {(running ? "running" : "not running")}\n\tspawn type = {spawnType}\n}}\n";

    static readonly Func<TimeSpan> Plenty = static () => TimeSpan.FromMinutes(1);

    /// <param name="bootstrapExits">Exit code of each successive bootstrap; the last repeats.</param>
    /// <param name="bootoutExit">A non-zero exit leaves the job loaded; zero unloads it.</param>
    /// <param name="bootstrapTimesOut">Each bootstrap reports a timeout; it loads the job unless it is
    /// already loaded.</param>
    LaunchdServiceManager Manager(
            List<string[]> calls, string spawnType, bool running = true, int bootoutExit = 0,
            bool printFails = false, bool bootstrapTimesOut = false, params int[] bootstrapExits) {
        var bootstraps = 0;
        var loaded     = true;

        return new(Home, TimeProvider.System,
            writeUnit: (path, content, _) => File.WriteAllText(path, content),
            runBounded: (_, args, _) => {
                calls.Add(args);
                switch (args[0]) {
                    case "print" when printFails:
                        return (5, "", "Input/output error", false);
                    case "print":
                        return loaded ? (0, Print(spawnType, running), "", false) : (113, "", "Could not find service", false);
                    case "bootstrap" when bootstrapTimesOut:
                        if (!loaded) (loaded, spawnType) = (true, "daemon (3)");
                        return (137, "", "", true);
                    case "bootout":
                        if (bootoutExit == 0) loaded = false;
                        return (bootoutExit, "", "", false);
                    case "bootstrap":
                        var exit = bootstrapExits.Length == 0 ? 0 : bootstrapExits[Math.Min(bootstraps++, bootstrapExits.Length - 1)];
                        if (exit == 0) (loaded, spawnType) = (true, "daemon (3)");
                        return (exit, "", exit == 0 ? "" : "Bootstrap failed: 5: Input/output error", false);
                    default:
                        return (0, "", "", false);
                }
            });
    }

    [Test]
    public async Task UpgradeProcessType_switches_the_adaptive_line_and_leaves_the_rest() {
        var upgraded = LaunchdUnit.UpgradeProcessType(AdaptivePlist());

        await Assert.That(upgraded).IsEqualTo(LaunchdUnit.Plist(Spec()));
        await Assert.That(LaunchdUnit.UpgradeProcessType(LaunchdUnit.Plist(Spec()))).IsNull();
    }

    [Test]
    public async Task LoadedSpawnType_reads_the_spawn_type_line() {
        await Assert.That(LaunchdUnit.LoadedSpawnType(Print("adaptive (6)"))).IsEqualTo("adaptive");
        await Assert.That(LaunchdUnit.LoadedSpawnType(Print("daemon (3)"))).IsEqualTo("daemon");
    }

    [Test]
    public async Task Idle_adaptive_job_is_rewritten_then_booted_out_and_bootstrapped() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");

        var path  = Seed(AdaptivePlist());
        var calls = new List<string[]>();
        var asked = 0;

        var outcome = Manager(calls, "adaptive (6)").RefreshUnit("test", () => { asked++; return true; }, Plenty, out var error);

        await Assert.That(outcome).IsEqualTo(UnitRefresh.Reloaded);
        await Assert.That(error).IsNull();
        await Assert.That(asked).IsEqualTo(1);
        await Assert.That(File.ReadAllText(path)).IsEqualTo(LaunchdUnit.Plist(Spec()));
        await Assert.That(calls.Select(c => c[0]).ToArray()).IsEquivalentTo(["print", "bootout", "bootstrap"]);
    }

    [Test]
    public async Task Busy_daemon_is_not_reloaded_but_its_plist_is_rewritten() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");

        var path  = Seed(AdaptivePlist());
        var calls = new List<string[]>();

        var outcome = Manager(calls, "adaptive (6)").RefreshUnit("test", () => false, Plenty, out _);

        await Assert.That(outcome).IsEqualTo(UnitRefresh.Deferred);
        await Assert.That(File.ReadAllText(path)).IsEqualTo(LaunchdUnit.Plist(Spec()));
        await Assert.That(calls.Select(c => c[0]).ToArray()).IsEquivalentTo(["print"]);
    }

    /// <summary>A deferred refresh leaves the plist current and the loaded job Adaptive; the next run
    /// must still reload it, keyed on the loaded spawn type rather than the file.</summary>
    [Test]
    public async Task Rewritten_plist_with_an_adaptive_loaded_job_is_still_reloaded() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");

        Seed(LaunchdUnit.Plist(Spec()));
        var calls = new List<string[]>();

        var outcome = Manager(calls, "adaptive (6)").RefreshUnit("test", () => true, Plenty, out _);

        await Assert.That(outcome).IsEqualTo(UnitRefresh.Reloaded);
    }

    [Test]
    public async Task Current_job_is_left_alone_and_the_daemon_is_not_asked() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");

        Seed(LaunchdUnit.Plist(Spec()));
        var calls = new List<string[]>();
        var asked = false;

        var outcome = Manager(calls, "daemon (3)").RefreshUnit("test", () => asked = true, Plenty, out _);

        await Assert.That(outcome).IsEqualTo(UnitRefresh.Unchanged);
        await Assert.That(asked).IsFalse();
        await Assert.That(calls.Select(c => c[0]).ToArray()).IsEquivalentTo(["print"]);
    }

    [Test]
    public async Task Failed_bootstrap_restores_the_original_plist_and_reloads_it() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");

        var path  = Seed(AdaptivePlist());
        var calls = new List<string[]>();

        var outcome = Manager(calls, "adaptive (6)", running: true, bootoutExit: 0, false, false, 5, 0).RefreshUnit("test", () => true, Plenty, out var error);

        await Assert.That(outcome).IsEqualTo(UnitRefresh.Failed);
        await Assert.That(error).Contains("previous unit was restored and loaded");
        await Assert.That(File.ReadAllText(path)).IsEqualTo(AdaptivePlist());
        await Assert.That(calls.Select(c => c[0]).ToArray()).IsEquivalentTo(["print", "bootout", "bootstrap", "bootstrap"]);
    }

    [Test]
    public async Task Failed_rollback_says_the_daemon_is_not_loaded() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");

        Seed(AdaptivePlist());

        var outcome = Manager([], "adaptive (6)", running: true, bootoutExit: 0, false, false, 5).RefreshUnit("test", () => true, Plenty, out var error);

        await Assert.That(outcome).IsEqualTo(UnitRefresh.Failed);
        await Assert.That(error).Contains("the daemon is not loaded");
    }

    /// <summary>A job a failed bootout left loaded is still running its old definition; bootstrapping
    /// over it would fail and misreport the daemon as unloaded.</summary>
    [Test]
    public async Task Bootout_that_leaves_the_job_loaded_skips_the_bootstrap() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");

        Seed(AdaptivePlist());
        var calls = new List<string[]>();

        var outcome = Manager(calls, "adaptive (6)", running: true, bootoutExit: 5).RefreshUnit("test", () => true, Plenty, out var error);

        await Assert.That(outcome).IsEqualTo(UnitRefresh.Failed);
        await Assert.That(error).Contains("did not unload");
        await Assert.That(calls.Select(c => c[0]).ToArray()).IsEquivalentTo(["print", "bootout", "print"]);
    }

    [Test]
    public async Task Reload_is_not_started_without_time_to_finish_it() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");

        var path  = Seed(AdaptivePlist());
        var calls = new List<string[]>();
        var asked = false;

        var outcome = Manager(calls, "adaptive (6)").RefreshUnit(
            "test", () => asked = true, () => LaunchdServiceManager.ReloadBudget - TimeSpan.FromSeconds(1), out _);

        await Assert.That(outcome).IsEqualTo(UnitRefresh.Deferred);
        await Assert.That(asked).IsFalse();
        await Assert.That(File.ReadAllText(path)).IsEqualTo(LaunchdUnit.Plist(Spec()));
        await Assert.That(calls.Select(c => c[0]).ToArray()).IsEquivalentTo(["print"]);
    }

    [Test]
    public async Task Loaded_job_with_no_running_daemon_is_reloaded_without_asking_it() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");

        Seed(AdaptivePlist());
        var asked = false;

        var outcome = Manager([], "adaptive (6)", running: false).RefreshUnit("test", () => asked = true, Plenty, out _);

        await Assert.That(outcome).IsEqualTo(UnitRefresh.Reloaded);
        await Assert.That(asked).IsFalse();
    }

    /// <summary>A plist whose ProcessType is a value this writer does not upgrade is left alone, and the
    /// daemon is neither asked nor restarted.</summary>
    [Test]
    public async Task Adaptive_plist_this_writer_cannot_upgrade_is_left_alone() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");

        var foreign = AdaptivePlist().Replace("<string>Adaptive</string>", "<string>Interactive</string>");
        var path  = Seed(foreign);
        var calls = new List<string[]>();
        var asked = false;

        var outcome = Manager(calls, "adaptive (6)").RefreshUnit("test", () => asked = true, Plenty, out _);

        await Assert.That(outcome).IsEqualTo(UnitRefresh.Unchanged);
        await Assert.That(asked).IsFalse();
        await Assert.That(File.ReadAllText(path)).IsEqualTo(foreign);
        await Assert.That(calls.Select(c => c[0]).ToArray()).IsEquivalentTo(["print"]);
    }

    [Test]
    public async Task DeclaresStandardProcessType_ignores_formatting() {
        await Assert.That(LaunchdUnit.DeclaresStandardProcessType(LaunchdUnit.Plist(Spec()))).IsTrue();
        await Assert.That(LaunchdUnit.DeclaresStandardProcessType(
            LaunchdUnit.Plist(Spec()).Replace("<string>Standard</string>", "\n\t<string>Standard</string>"))).IsTrue();
        await Assert.That(LaunchdUnit.DeclaresStandardProcessType(AdaptivePlist())).IsFalse();
        await Assert.That(LaunchdUnit.DeclaresStandardProcessType("not xml")).IsFalse();
    }

    [Test]
    public async Task Unreadable_launchd_state_rewrites_the_plist_but_reloads_nothing() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");

        var path  = Seed(AdaptivePlist());
        var calls = new List<string[]>();
        var asked = false;

        var outcome = Manager(calls, "adaptive (6)", printFails: true).RefreshUnit("test", () => asked = true, Plenty, out _);

        await Assert.That(outcome).IsEqualTo(UnitRefresh.Unverified);
        await Assert.That(asked).IsFalse();
        await Assert.That(File.ReadAllText(path)).IsEqualTo(LaunchdUnit.Plist(Spec()));
        await Assert.That(calls.Select(c => c[0]).ToArray()).IsEquivalentTo(["print"]);
    }

    /// <summary>A bootstrap can load the job and still overrun its timeout; rolling back then would put
    /// the Adaptive plist on disk under a job that loaded as Standard.</summary>
    [Test]
    public async Task Timed_out_bootstrap_that_loaded_the_job_counts_as_reloaded() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");

        var path = Seed(AdaptivePlist());

        var outcome = Manager([], "adaptive (6)", bootstrapTimesOut: true).RefreshUnit("test", () => true, Plenty, out var error);

        await Assert.That(outcome).IsEqualTo(UnitRefresh.Reloaded);
        await Assert.That(error).IsNull();
        await Assert.That(File.ReadAllText(path)).IsEqualTo(LaunchdUnit.Plist(Spec()));
    }

    /// <summary>A bootout that did not take leaves the old job loaded, so a timed-out bootstrap that then
    /// finds the label loaded as Adaptive has not reloaded anything.</summary>
    [Test]
    public async Task Timed_out_bootstrap_over_the_old_adaptive_job_is_not_a_reload() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");

        Seed(AdaptivePlist());
        var calls = new List<string[]>();

        var manager = new LaunchdServiceManager(Home, TimeProvider.System,
            writeUnit: (path, content, _) => File.WriteAllText(path, content),
            runBounded: (_, args, _) => {
                calls.Add(args);
                return args[0] switch {
                    "print"     => calls.Count(c => c[0] == "print") == 2
                                       ? (5, "", "Input/output error", false)
                                       : (0, Print("adaptive (6)"), "", false),
                    "bootout"   => (0, "", "", true),
                    "bootstrap" => (137, "", "", true),
                    _           => (0, "", "", false),
                };
            });

        var outcome = manager.RefreshUnit("test", () => true, Plenty, out var error);

        await Assert.That(outcome).IsEqualTo(UnitRefresh.Failed);
        await Assert.That(error).Contains("restored and loaded");
        // Six calls after the daemon is asked, the most ReloadBudget reserves for.
        await Assert.That(calls.Select(c => c[0]).ToArray())
            .IsEquivalentTo(["print", "bootout", "print", "bootstrap", "print", "bootstrap", "print"]);
    }
}
