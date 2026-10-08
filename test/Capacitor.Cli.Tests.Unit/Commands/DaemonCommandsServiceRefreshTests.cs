using Capacitor.Cli.Commands;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>A forced refresh targets one daemon, asks it for a forced restart, and reports exactly one
/// machine-readable outcome line; the unforced run keeps iterating every installed unit.</summary>
[NotInParallel]
public class DaemonCommandsServiceRefreshTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static ServiceSpec Spec(string id) => new(id, "/opt/kcap/kcap-daemon", $"/home/u/.config/kcap/daemon-{id}.log", new Dictionary<string, string>(), []);

    string Seed(string id, string content) {
        Directory.CreateDirectory(LaunchdUnit.AgentsDir(Home));
        var path = LaunchdUnit.PlistPath(Home, id);
        File.WriteAllText(path, content);
        return path;
    }

    static string Print(string id, string spawnType) =>
        $"gui/501/io.kurrent.kcap.daemon.{id} = {{\n\tstate = running\n\tspawn type = {spawnType}\n}}\n";

    LaunchdServiceManager Manager(List<string[]> calls, Dictionary<string, string> spawnTypes) =>
        new(Home, TimeProvider.System,
            writeUnit: (p, c, _) => File.WriteAllText(p, c),
            runBounded: (_, args, _) => {
                calls.Add(args);
                var id = args[^1].Split('.')[^1];
                return args[0] switch {
                    "print" when spawnTypes.TryGetValue(id, out var spawn) => (0, Print(id, spawn), "", false),
                    "print"     => (113, "", "Could not find service", false),
                    "bootstrap" => ((Func<(int, string, string, bool)>)(() => { spawnTypes[Path.GetFileNameWithoutExtension(args[^1]).Split('.')[^1]] = "daemon (3)"; return (0, "", "", false); }))(),
                    _           => (0, "", "", false),
                };
            });

    DaemonServiceCommands Commands(LaunchdServiceManager manager, string id, Func<string, string, bool> restart) =>
        new(Daemons.Store, Config.Root, Resolutions.None(Config.Root), manager, id, Home, TimeProvider.System) { RestartRequester = restart };

    [Test]
    public async Task Forced_run_reloads_only_the_named_daemon_and_prints_one_token() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Seed("a", LaunchdUnit.Plist(Spec("a")).Replace("Standard", "Adaptive"));
        Seed("b", LaunchdUnit.Plist(Spec("b")).Replace("Standard", "Adaptive"));
        var calls = new List<string[]>();
        var modes = new List<(string Id, string Mode)>();
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(Manager(calls, new() { ["a"] = "adaptive (6)", ["b"] = "adaptive (6)" }), "a",
            (id, mode) => { modes.Add((id, mode)); return true; }).Refresh(force: true, stabilize: s => s);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(modes).IsEquivalentTo([("a", "force")]);
        await Assert.That(calls.All(c => c[^1].EndsWith(".a", StringComparison.Ordinal) || c[^1].EndsWith(".a.plist", StringComparison.Ordinal))).IsTrue();
        var tokens = console.GetCapturedError().Split('\n').Where(l => l.StartsWith("refresh_outcome=", StringComparison.Ordinal)).ToArray();
        await Assert.That(tokens).IsEquivalentTo(["refresh_outcome=reloaded"]);
    }

    [Test]
    public async Task Forced_run_on_a_missing_unit_is_unit_missing_and_touches_nothing_else() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Seed("b", LaunchdUnit.Plist(Spec("b")).Replace("Standard", "Adaptive"));
        var calls = new List<string[]>();
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(Manager(calls, new() { ["b"] = "adaptive (6)" }), "a", (_, _) => true).Refresh(force: true, stabilize: s => s);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(console.GetCapturedError()).Contains("refresh_outcome=unit_missing");
        await Assert.That(calls).IsEmpty();
    }

    [Test]
    public async Task Forced_run_reports_contended_while_another_operation_holds_the_lock() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Seed("a", LaunchdUnit.Plist(Spec("a")).Replace("Standard", "Adaptive"));
        using var held = await ServiceTxnLock.TryAcquireAsync(Daemons.Store, "a", TimeSpan.Zero, TimeProvider.System);
        var calls = new List<string[]>();
        var asked = false;
        using var console = ConsoleOutput.StartFullCapture();

        var commands = Commands(Manager(calls, new() { ["a"] = "adaptive (6)" }), "a", (_, _) => asked = true);
        var exit = await commands.Refresh(force: true, stabilize: s => s);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(console.GetCapturedError()).Contains("refresh_outcome=contended");
        await Assert.That(asked).IsFalse();
        await Assert.That(calls).IsEmpty();
    }

    [Test]
    public async Task Unforced_run_iterates_every_unit_and_names_force_when_busy() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Seed("a", LaunchdUnit.Plist(Spec("a")).Replace("Standard", "Adaptive"));
        Seed("b", LaunchdUnit.Plist(Spec("b")).Replace("Standard", "Adaptive"));
        var calls = new List<string[]>();
        var asked = new List<(string Id, string Mode)>();
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(Manager(calls, new() { ["a"] = "adaptive (6)", ["b"] = "adaptive (6)" }), "a",
            (id, mode) => { asked.Add((id, mode)); return false; }).Refresh(stabilize: s => s);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(asked).IsEquivalentTo([("a", "now"), ("b", "now")]);
        await Assert.That(console.GetCapturedOutput()).Contains("kcap daemon service refresh --name a --force");
        await Assert.That(console.GetCapturedError()).DoesNotContain("refresh_outcome=");
    }

    [Test]
    public async Task Token_mapping_and_exit_codes_follow_the_table() {
        await Assert.That(DaemonServiceCommands.RefreshToken(UnitRefresh.Reloaded)).IsEqualTo("reloaded");
        await Assert.That(DaemonServiceCommands.RefreshToken(UnitRefresh.Current)).IsEqualTo("current");
        await Assert.That(DaemonServiceCommands.RefreshToken(UnitRefresh.NotLoaded)).IsEqualTo("not_loaded");
        await Assert.That(DaemonServiceCommands.RefreshToken(UnitRefresh.Deferred)).IsEqualTo("deferred");
        await Assert.That(DaemonServiceCommands.RefreshToken(UnitRefresh.Contended)).IsEqualTo("contended");
        await Assert.That(DaemonServiceCommands.RefreshToken(UnitRefresh.Unverified)).IsEqualTo("unverified");
        await Assert.That(DaemonServiceCommands.RefreshToken(UnitRefresh.UnitMissing)).IsEqualTo("unit_missing");
        await Assert.That(DaemonServiceCommands.RefreshToken(UnitRefresh.UnitUnreadable)).IsEqualTo("unit_unreadable");
        await Assert.That(DaemonServiceCommands.RefreshToken(UnitRefresh.UnitUnsupported)).IsEqualTo("unit_unsupported");
        await Assert.That(DaemonServiceCommands.RefreshToken(UnitRefresh.Failed)).IsEqualTo("failed");
        await Assert.That(DaemonServiceCommands.ForcedExitCode(UnitRefresh.Current)).IsEqualTo(0);
        await Assert.That(DaemonServiceCommands.ForcedExitCode(UnitRefresh.Deferred)).IsEqualTo(1);
    }
}
