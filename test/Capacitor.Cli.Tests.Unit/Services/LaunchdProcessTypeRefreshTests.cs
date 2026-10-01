using System.Runtime.InteropServices;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>
/// A job installed as Adaptive gets its plist rewritten to Standard, but is reloaded only when the
/// daemon accepts an idle-only restart, because the reload kills whatever it hosts.
/// </summary>
public partial class LaunchdProcessTypeRefreshTests {
    [TempHome] public required TempHome Home { get; init; }

    [LibraryImport("libc", EntryPoint = "getuid")]
    private static partial uint getuid();

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

    /// <param name="bootstrapExits">Exit code of each successive bootstrap; the last repeats.</param>
    LaunchdServiceManager Manager(List<string[]> calls, string spawnType, bool running = true, params int[] bootstrapExits) {
        var bootstraps = 0;

        return new(Home, TimeProvider.System,
            writeUnit: (path, content, _) => File.WriteAllText(path, content),
            runBounded: (_, args, _) => {
                calls.Add(args);
                if (args[0] == "print") return (0, Print(spawnType, running), "", false);
                if (args[0] != "bootstrap") return (0, "", "", false);

                var exit = bootstrapExits.Length == 0 ? 0 : bootstrapExits[Math.Min(bootstraps++, bootstrapExits.Length - 1)];
                return (exit, "", exit == 0 ? "" : "Bootstrap failed: 5: Input/output error", false);
            });
    }

    [Test]
    public async Task UpgradeProcessType_switches_the_adaptive_line_and_leaves_the_rest() {
        var upgraded = LaunchdUnit.UpgradeProcessType(AdaptivePlist());

        await Assert.That(upgraded).IsEqualTo(LaunchdUnit.Plist(Spec()));
        await Assert.That(LaunchdUnit.UpgradeProcessType(LaunchdUnit.Plist(Spec()))).IsNull();
    }

    [Test]
    public async Task LoadedAsAdaptive_reads_the_spawn_type_line() {
        await Assert.That(LaunchdUnit.LoadedAsAdaptive(Print("adaptive (6)"))).IsTrue();
        await Assert.That(LaunchdUnit.LoadedAsAdaptive(Print("daemon (3)"))).IsFalse();
        await Assert.That(LaunchdUnit.LoadedAsAdaptive("")).IsFalse();
    }

    [Test]
    public async Task Idle_adaptive_job_is_rewritten_then_booted_out_and_bootstrapped() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");

        var path  = Seed(AdaptivePlist());
        var calls = new List<string[]>();
        var asked = 0;

        var outcome = Manager(calls, "adaptive (6)").RefreshProcessType("test", () => { asked++; return true; }, out var error);

        await Assert.That(outcome).IsEqualTo(ProcessTypeRefresh.Reloaded);
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

        var outcome = Manager(calls, "adaptive (6)").RefreshProcessType("test", () => false, out _);

        await Assert.That(outcome).IsEqualTo(ProcessTypeRefresh.Deferred);
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

        var outcome = Manager(calls, "adaptive (6)").RefreshProcessType("test", () => true, out _);

        await Assert.That(outcome).IsEqualTo(ProcessTypeRefresh.Reloaded);
    }

    [Test]
    public async Task Current_job_is_left_alone_and_the_daemon_is_not_asked() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");

        Seed(LaunchdUnit.Plist(Spec()));
        var calls = new List<string[]>();
        var asked = false;

        var outcome = Manager(calls, "daemon (3)").RefreshProcessType("test", () => asked = true, out _);

        await Assert.That(outcome).IsEqualTo(ProcessTypeRefresh.Unchanged);
        await Assert.That(asked).IsFalse();
        await Assert.That(calls.Select(c => c[0]).ToArray()).IsEquivalentTo(["print"]);
    }

    [Test]
    public async Task Failed_bootstrap_restores_the_original_plist_and_reloads_it() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");

        var path  = Seed(AdaptivePlist());
        var calls = new List<string[]>();

        var outcome = Manager(calls, "adaptive (6)", running: true, 5, 0).RefreshProcessType("test", () => true, out var error);

        await Assert.That(outcome).IsEqualTo(ProcessTypeRefresh.Failed);
        await Assert.That(error).Contains("previous unit was restored and loaded");
        await Assert.That(File.ReadAllText(path)).IsEqualTo(AdaptivePlist());
        await Assert.That(calls.Select(c => c[0]).ToArray()).IsEquivalentTo(["print", "bootout", "bootstrap", "bootstrap"]);
    }

    [Test]
    public async Task Failed_rollback_says_the_daemon_is_not_loaded() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");

        Seed(AdaptivePlist());

        var outcome = Manager([], "adaptive (6)", running: true, 5).RefreshProcessType("test", () => true, out var error);

        await Assert.That(outcome).IsEqualTo(ProcessTypeRefresh.Failed);
        await Assert.That(error).Contains("the daemon is not loaded");
    }

    [Test]
    public async Task Loaded_job_with_no_running_daemon_is_reloaded_without_asking_it() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");

        Seed(AdaptivePlist());
        var asked = false;

        var outcome = Manager([], "adaptive (6)", running: false).RefreshProcessType("test", () => asked = true, out _);

        await Assert.That(outcome).IsEqualTo(ProcessTypeRefresh.Reloaded);
        await Assert.That(asked).IsFalse();
    }

    /// <summary>A plist whose Adaptive value is not in this writer's exact line would reload as Adaptive,
    /// so the daemon is neither asked nor restarted.</summary>
    [Test]
    public async Task Adaptive_plist_this_writer_cannot_upgrade_is_left_alone() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");

        var foreign = AdaptivePlist().Replace("<key>ProcessType</key><string>Adaptive</string>",
            "<key>ProcessType</key>\n\t<string>Adaptive</string>");
        var path  = Seed(foreign);
        var calls = new List<string[]>();
        var asked = false;

        var outcome = Manager(calls, "adaptive (6)").RefreshProcessType("test", () => asked = true, out _);

        await Assert.That(outcome).IsEqualTo(ProcessTypeRefresh.Unchanged);
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
}
