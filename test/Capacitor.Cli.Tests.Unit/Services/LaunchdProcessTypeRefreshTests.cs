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

    static string Print(string spawnType) => $"gui/501/{Label} = {{\n\tstate = running\n\tspawn type = {spawnType}\n}}\n";

    LaunchdServiceManager Manager(List<string[]> calls, string spawnType, int bootstrapExit = 0) =>
        new(Home, TimeProvider.System,
            writeUnit: (path, content, _) => File.WriteAllText(path, content),
            runProcess: (_, args) => {
                calls.Add(args);
                return args[0] switch {
                    "print"     => (0, Print(spawnType), ""),
                    "bootstrap" => (bootstrapExit, "", bootstrapExit == 0 ? "" : "Bootstrap failed: 5: Input/output error"),
                    _           => (0, "", ""),
                };
            });

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

        var outcome = Manager(calls, "adaptive (6)", bootstrapExit: 5).RefreshProcessType("test", () => true, out var error);

        await Assert.That(outcome).IsEqualTo(ProcessTypeRefresh.Failed);
        await Assert.That(error).Contains("bootstrap failed");
        await Assert.That(File.ReadAllText(path)).IsEqualTo(AdaptivePlist());
        await Assert.That(calls.Select(c => c[0]).ToArray()).IsEquivalentTo(["print", "bootout", "bootstrap", "bootstrap"]);
    }
}
