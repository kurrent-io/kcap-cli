using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>
/// A unit installed from a script install's version directory pins that version, because the process
/// path resolves through <c>current</c>. <c>daemon service refresh</c> moves it onto the stable path:
/// launchd reloads it only when the daemon accepts an idle restart, systemd only reloads its copy.
/// </summary>
public class ServiceRepointTests {
    [TempHome] public required TempHome Home { get; init; }

    const string Label  = "io.kurrent.kcap.daemon.test";
    const string Pinned = "/home/u/.local/share/kcap/versions/1.0.0/bin/kcap-daemon";
    const string Stable = "/home/u/.local/share/kcap/current/bin/kcap-daemon";

    static string Stabilize(string path) => path == Pinned ? Stable : path;

    static ServiceSpec Spec(string binary) => new(
        ServiceId: "test", DaemonBinaryPath: binary, LogPath: "/home/u/.config/kcap/daemon-test.log",
        Environment: new Dictionary<string, string> { ["KCAP_PROFILE"] = "work" }, ExtraArgs: ["--max-agents", "4"]);

    static readonly Func<TimeSpan> Plenty = static () => TimeSpan.FromMinutes(1);

    [Test]
    public async Task WithBinary_replaces_only_the_program_and_keeps_every_other_byte() {
        var rewritten = LaunchdUnit.WithBinary(LaunchdUnit.Plist(Spec(Pinned)), Stable);

        await Assert.That(rewritten).IsEqualTo(LaunchdUnit.Plist(Spec(Stable)));
        await Assert.That(LaunchdUnit.WithBinary(LaunchdUnit.Plist(Spec(Stable)), Stable)).IsNull();
    }

    [Test]
    public async Task LoadedProgram_reads_the_print_line() {
        await Assert.That(LaunchdUnit.LoadedProgram($"{Label} = {{\n\tprogram = {Pinned}\n}}")).IsEqualTo(Pinned);
        await Assert.That(LaunchdUnit.LoadedProgram("")).IsNull();
    }

    string Seed(string binary) {
        Directory.CreateDirectory(LaunchdUnit.AgentsDir(Home));
        var path = LaunchdUnit.PlistPath(Home, "test");
        File.WriteAllText(path, LaunchdUnit.Plist(Spec(binary)));
        return path;
    }

    LaunchdServiceManager Launchd(List<string[]> calls, string loadedProgram, bool running = true) {
        var loaded = true;

        return new(Home, TimeProvider.System,
            writeUnit: (path, content, _) => File.WriteAllText(path, content),
            runBounded: (_, args, _) => {
                calls.Add(args);
                switch (args[0]) {
                    case "print":
                        return loaded
                            ? (0, $"gui/501/{Label} = {{\n\tstate = {(running ? "running" : "not running")}\n\tprogram = {loadedProgram}\n\tspawn type = daemon (3)\n}}\n", "", false)
                            : (113, "", "Could not find service", false);
                    case "bootout":
                        loaded = false;
                        return (0, "", "", false);
                    case "bootstrap":
                        (loaded, loadedProgram) = (true, LaunchdUnit.BinaryFromPlist(File.ReadAllText(LaunchdUnit.PlistPath(Home, "test")))!);
                        return (0, "", "", false);
                    default:
                        return (0, "", "", false);
                }
            });
    }

    [Test]
    public async Task An_idle_pinned_job_is_moved_to_current_and_reloaded() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        var path  = Seed(Pinned);
        var calls = new List<string[]>();

        var outcome = Launchd(calls, Pinned).RefreshUnit("test", () => true, Plenty, out var error, Stabilize);

        await Assert.That(outcome).IsEqualTo(UnitRefresh.Reloaded);
        await Assert.That(error).IsNull();
        await Assert.That(LaunchdUnit.BinaryFromPlist(File.ReadAllText(path))).IsEqualTo(Stable);
        await Assert.That(calls.Select(c => c[0]).ToArray()).IsEquivalentTo(["print", "bootout", "bootstrap"]);
    }

    /// <summary>A reload kills whatever the daemon hosts, so a busy one keeps its old binary until later.</summary>
    [Test]
    public async Task A_busy_pinned_job_has_its_plist_moved_but_is_not_reloaded() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        var path  = Seed(Pinned);
        var calls = new List<string[]>();

        var outcome = Launchd(calls, Pinned).RefreshUnit("test", () => false, Plenty, out _, Stabilize);

        await Assert.That(outcome).IsEqualTo(UnitRefresh.Deferred);
        await Assert.That(LaunchdUnit.BinaryFromPlist(File.ReadAllText(path))).IsEqualTo(Stable);
        await Assert.That(calls.Select(c => c[0]).ToArray()).IsEquivalentTo(["print"]);
    }

    /// <summary>The plist moved on an earlier, deferred run; the loaded job still runs the version directory.</summary>
    [Test]
    public async Task A_job_still_loaded_from_a_version_directory_is_reloaded_on_a_later_run() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Seed(Stable);

        var outcome = Launchd([], Pinned).RefreshUnit("test", () => true, Plenty, out _, Stabilize);

        await Assert.That(outcome).IsEqualTo(UnitRefresh.Reloaded);
    }

    [Test]
    public async Task A_job_already_on_current_is_left_alone() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Seed(Stable);
        var asked = false;

        var outcome = Launchd([], Stable).RefreshUnit("test", () => asked = true, Plenty, out _, Stabilize);

        await Assert.That(outcome).IsEqualTo(UnitRefresh.Unchanged);
        await Assert.That(asked).IsFalse();
    }

    [Test]
    public async Task Systemd_WithBinary_replaces_only_the_execstart_program() {
        var rewritten = SystemdUnit.WithBinary(SystemdUnit.Unit(Spec(Pinned)), "/home/u/my kcap/current/bin/kcap-daemon");

        await Assert.That(rewritten).IsEqualTo(SystemdUnit.Unit(Spec("/home/u/my kcap/current/bin/kcap-daemon")));
        await Assert.That(SystemdUnit.WithBinary(SystemdUnit.Unit(Spec(Stable)), Stable)).IsNull();
    }

    [Test]
    public async Task Systemd_repoint_rewrites_the_unit_and_reloads_systemd_without_restarting() {
        var path = SystemdUnit.UnitPath(Home, "test");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, SystemdUnit.Unit(Spec(Pinned)));
        var calls   = new List<string[]>();
        var systemd = new SystemdServiceManager(Home, (p, c, _) => File.WriteAllText(p, c), (_, args) => { calls.Add(args); return (0, "", ""); });

        var moved = systemd.Repoint("test", Stabilize);

        await Assert.That(moved).IsTrue();
        await Assert.That(File.ReadAllText(path)).IsEqualTo(SystemdUnit.Unit(Spec(Stable)));
        await Assert.That(calls.Select(c => string.Join(' ', c)).ToArray()).IsEquivalentTo(["--user daemon-reload"]);
        await Assert.That(systemd.Repoint("test", Stabilize)).IsFalse();
    }
}
