using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Services;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>A forced refresh targets one daemon, stops it for the reload, and reports exactly one
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

    LaunchdServiceManager Manager(List<string[]> calls, Dictionary<string, string> spawnTypes, Action<string[]>? onCall = null) =>
        new(Home, TimeProvider.System,
            writeUnit: (p, c, _) => File.WriteAllText(p, c),
            runBounded: (_, args, _) => {
                calls.Add(args);
                onCall?.Invoke(args);
                var id = args[^1].Split('.')[^1];
                return args[0] switch {
                    "print" when spawnTypes.TryGetValue(id, out var spawn) => (0, Print(id, spawn), "", false),
                    "print"     => (113, "", "Could not find service", false),
                    "bootstrap" => ((Func<(int, string, string, bool)>)(() => { spawnTypes[Path.GetFileNameWithoutExtension(args[^1]).Split('.')[^1]] = "daemon (3)"; return (0, "", "", false); }))(),
                    _           => (0, "", "", false),
                };
            });

    DaemonServiceCommands Commands(LaunchdServiceManager manager, string id, Func<string, ReloadStop>? stopForReload) =>
        new(Daemons.Store, Config.Root, Resolutions.None(Config.Root), manager, id, Home, TimeProvider.System) { ReloadStopper = stopForReload };

    [Test]
    public async Task Forced_run_reloads_only_the_named_daemon_and_prints_one_token() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Seed("a", LaunchdUnit.Plist(Spec("a")).Replace("Standard", "Adaptive"));
        Seed("b", LaunchdUnit.Plist(Spec("b")).Replace("Standard", "Adaptive"));
        var calls = new List<string[]>();
        var stopped = new List<string>();
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(Manager(calls, new() { ["a"] = "adaptive (6)", ["b"] = "adaptive (6)" }), "a",
            id => { stopped.Add(id); return ReloadStop.Exited; }).Refresh(force: true, stabilize: s => s);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(stopped).IsEquivalentTo(["a"]);
        await Assert.That(calls.All(c => c[^1].EndsWith(".a", StringComparison.Ordinal) || c[^1].EndsWith(".a.plist", StringComparison.Ordinal))).IsTrue();
        var tokens = console.GetCapturedError().Split('\n').Where(l => l.StartsWith("refresh_outcome=", StringComparison.Ordinal)).ToArray();
        await Assert.That(tokens).IsEquivalentTo(["refresh_outcome=reloaded"]);
    }

    [Test]
    public async Task An_unknown_spawn_type_is_unverified_when_forced_and_silent_when_not() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Seed("a", LaunchdUnit.Plist(Spec("a")));

        using (var forced = ConsoleOutput.StartFullCapture()) {
            var exit = await Commands(Manager([], new() { ["a"] = "app (1)" }), "a", _ => ReloadStop.Exited).Refresh(force: true, stabilize: s => s);
            await Assert.That(exit).IsNotEqualTo(0);
            await Assert.That(forced.GetCapturedError()).Contains("refresh_outcome=unverified");
        }

        using var unforced = ConsoleOutput.StartFullCapture();
        var unforcedExit = await Commands(Manager([], new() { ["a"] = "app (1)" }), "a", _ => ReloadStop.Exited).Refresh(stabilize: s => s);
        await Assert.That(unforcedExit).IsEqualTo(0);
        await Assert.That(unforced.GetCapturedOutput().Trim()).IsEqualTo("");
        await Assert.That(unforced.GetCapturedError().Trim()).IsEqualTo("");
    }

    [Test]
    public async Task Forced_run_does_not_charge_the_lock_wait_to_the_reload_budget() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Seed("a", LaunchdUnit.Plist(Spec("a")).Replace("Standard", "Adaptive"));
        var time = new FakeTimeProvider();
        var held = await ServiceTxnLock.TryAcquireAsync(Daemons.Store, "a", TimeSpan.Zero, time);
        using var console = ConsoleOutput.StartFullCapture();
        var commands = new DaemonServiceCommands(Daemons.Store, Config.Root, Resolutions.None(Config.Root),
            Manager([], new() { ["a"] = "adaptive (6)" }), "a", Home, time) { ReloadStopper = _ => ReloadStop.Exited };

        var run     = commands.Refresh(force: true, stabilize: s => s);
        var release = time.GetUtcNow().AddSeconds(9);
        for (var steps = 0; !run.IsCompleted; steps++) {
            if (steps > 400) throw new TimeoutException("the forced refresh never finished under the fake clock");
            if (held is not null && time.GetUtcNow() >= release) { held.Dispose(); held = null; }
            time.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Delay(5);
        }
        held?.Dispose();

        await Assert.That(await run).IsEqualTo(0);
        await Assert.That(console.GetCapturedError()).Contains("refresh_outcome=reloaded");
    }

    [Test]
    public async Task Forced_run_off_launchd_is_unsupported_and_touches_no_unit() {
        var manager = new UntouchableManager();
        using var console = ConsoleOutput.StartFullCapture();
        var commands = new DaemonServiceCommands(Daemons.Store, Config.Root, Resolutions.None(Config.Root), manager, "a", Home, TimeProvider.System) {
            ReloadStopper = _ => ReloadStop.Exited,
        };

        var exit = await commands.Refresh(force: true, stabilize: s => s);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(console.GetCapturedError()).Contains("refresh_outcome=unsupported");
    }

    /// A non-launchd manager the forced run must never touch: every member throws.
    sealed class UntouchableManager : IServiceManager {
        static Exception Touched() => new InvalidOperationException("the forced run touched the service manager");
        public string Describe() => throw Touched();
        public string UnitDirectory => throw Touched();
        public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) => throw Touched();
        public IReadOnlyList<string> ListInstalled() => throw Touched();
        public ServiceStatus Status(string serviceId) => throw Touched();
        public IReadOnlyDictionary<string, string>? UnitEnvironment(string serviceId) => throw Touched();
        public ServiceQuery Query(string serviceId) => throw Touched();
        public bool Install(ServiceSpec spec, bool startNow, out string? error) => throw Touched();
        public void WriteAndBootstrap(ServiceSpec spec) => throw Touched();
        public bool Uninstall(string serviceId, out string? error) => throw Touched();
        public bool Start(string serviceId, out string? error) => throw Touched();
        public bool Stop(string serviceId, out string? error) => throw Touched();
    }

    [Test]
    public async Task Forced_run_on_a_missing_unit_is_unit_missing_and_touches_nothing_else() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Seed("b", LaunchdUnit.Plist(Spec("b")).Replace("Standard", "Adaptive"));
        var calls = new List<string[]>();
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(Manager(calls, new() { ["b"] = "adaptive (6)" }), "a", _ => ReloadStop.Exited).Refresh(force: true, stabilize: s => s);

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

        var commands = Commands(Manager(calls, new() { ["a"] = "adaptive (6)" }), "a", _ => { asked = true; return ReloadStop.Exited; });
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
        var asked = new List<string>();
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(Manager(calls, new() { ["a"] = "adaptive (6)", ["b"] = "adaptive (6)" }), "a",
            id => { asked.Add(id); return ReloadStop.NoAnswer; }).Refresh(stabilize: s => s);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(asked).IsEquivalentTo(["a", "b"]);
        await Assert.That(console.GetCapturedOutput()).Contains("did not answer the stop request");
        await Assert.That(console.GetCapturedOutput()).Contains("kcap daemon service refresh --name a --force");
        await Assert.That(console.GetCapturedError()).DoesNotContain("refresh_outcome=");
    }

    [Test]
    public async Task Refresh_waits_for_the_exit_before_bootout() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Seed("a", LaunchdUnit.Plist(Spec("a")).Replace("Standard", "Adaptive"));
        using var child = DummyProcess.StartSleep(120);
        var identity = PidIdentity.Capture(child.Pid);
        File.WriteAllText(Daemons.Store.PidPath("a"), $"{child.Pid}\n{ProcessStartToken.ForPid(child.Pid)}\n");
        var received = FakeControlSocket.AnswerOnce(Daemons.Store, "a", LocalFrame.ShutdownAck("stopping"),
            afterReply: () => _ = Task.Delay(300).ContinueWith(_ => child.Kill(), TaskScheduler.Default));
        var aliveAtBootout = new List<bool>();
        var manager = Manager([], new() { ["a"] = "adaptive (6)" },
            onCall: args => { if (args[0] == "bootout") aliveAtBootout.Add(!PidIdentity.IsGone(child.Pid, identity)); });
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(manager, "a", stopForReload: null).Refresh(stabilize: s => s);

        await Assert.That(exit).IsEqualTo(0);
        var request = await received.WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(ShutdownIpc.Parse(request!.Text!)).IsEqualTo(new ShutdownRequestDto("keep", "final", WhenIdle: true));
        await Assert.That(aliveAtBootout).IsEquivalentTo([false]);
    }

    /// <summary>The unattended pass asks the daemon to stop only when idle; a busy daemon keeps running
    /// and its unit change waits.</summary>
    [Test]
    public async Task An_unattended_refresh_of_a_busy_daemon_defers() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Seed("a", LaunchdUnit.Plist(Spec("a")).Replace("Standard", "Adaptive"));
        using var child = StartDaemon("a");
        var received = FakeControlSocket.AnswerOnce(Daemons.Store, "a", LocalFrame.Error("busy: an eval or a launch is in progress"));
        var calls = new List<string[]>();
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(Manager(calls, new() { ["a"] = "adaptive (6)" }), "a", stopForReload: null).Refresh(stabilize: s => s);

        await Assert.That(exit).IsEqualTo(0);
        var request = await received.WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(ShutdownIpc.Parse(request!.Text!)!.WhenIdle).IsTrue();
        await Assert.That(calls.Any(c => c[0] == "bootout")).IsFalse();
        await Assert.That(console.GetCapturedOutput()).Contains("is busy, so its service unit change waits");
    }

    [Test]
    public async Task A_forced_refresh_stops_the_daemon_without_waiting_for_idle() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Seed("a", LaunchdUnit.Plist(Spec("a")).Replace("Standard", "Adaptive"));
        using var child = StartDaemon("a");
        var received = FakeControlSocket.AnswerOnce(Daemons.Store, "a", LocalFrame.ShutdownAck("stopping"),
            afterReply: () => { child.Kill(); child.WaitForExit(); });
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(Manager([], new() { ["a"] = "adaptive (6)" }), "a", stopForReload: null).Refresh(force: true, stabilize: s => s);

        await Assert.That(exit).IsEqualTo(0);
        var request = await received.WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(ShutdownIpc.Parse(request!.Text!)).IsEqualTo(new ShutdownRequestDto("keep", "final"));
    }

    DummyProcess StartDaemon(string id) {
        var child = DummyProcess.StartSleep(120);
        File.WriteAllText(Daemons.Store.PidPath(id), $"{child.Pid}\n{ProcessStartToken.ForPid(child.Pid)}\n");
        return child;
    }

    /// <summary>A daemon from before the shutdown frame cannot decode it and closes the connection without
    /// a reply; the reload asks it for a restart in the old mode instead and reloads once it accepts. An
    /// explicit Error reply is treated the same.</summary>
    [Test]
    [Arguments(false, "now", false)]
    [Arguments(true, "force", false)]
    [Arguments(false, "now", true)]
    public async Task A_daemon_that_refuses_shutdown_is_asked_for_a_restart_instead(bool force, string mode, bool explicitError) {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Seed("a", LaunchdUnit.Plist(Spec("a")).Replace("Standard", "Adaptive"));
        using var child = StartDaemon("a");
        var refusal  = explicitError ? LocalFrame.Error("expected Spawn/Attach/List, got Shutdown") : null;
        var received = FakeControlSocket.AnswerInOrder(Daemons.Store, "a", [refusal, LocalFrame.RestartAck("restarting")]);
        var calls = new List<string[]>();
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(Manager(calls, new() { ["a"] = "adaptive (6)" }), "a", stopForReload: null)
            .Refresh(force: force, stabilize: s => s);

        await Assert.That(exit).IsEqualTo(0);
        var frames = await received.WaitAsync(TimeSpan.FromSeconds(30));
        await Assert.That(frames[0]!.Type).IsEqualTo(FrameType.Shutdown);
        await Assert.That(frames[1] is { Type: FrameType.Restart } restart && restart.Text == mode).IsTrue();
        await Assert.That(calls.Count(c => c[0] == "bootout")).IsEqualTo(1);
        await Assert.That(console.GetCapturedOutput()).Contains("reloaded");
    }

    /// <summary>A daemon that accepted the stop but is still exiting at the bound is ended by the bootout,
    /// and the reload goes ahead.</summary>
    [Test]
    public async Task An_accepted_stop_that_overruns_the_wait_still_reloads() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        Seed("a", LaunchdUnit.Plist(Spec("a")).Replace("Standard", "Adaptive"));
        using var child = StartDaemon("a");
        var acked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = FakeControlSocket.AnswerOnce(Daemons.Store, "a", LocalFrame.ShutdownAck("stopping"), afterReply: acked.SetResult);
        var calls   = new List<string[]>();
        var manager = Manager(calls, new() { ["a"] = "adaptive (6)" },
            onCall: args => { if (args[0] == "bootout") { child.Kill(); child.WaitForExit(); } });
        var time     = new FakeTimeProvider();
        var commands = new DaemonServiceCommands(Daemons.Store, Config.Root, Resolutions.None(Config.Root), manager, "a", Home, time);
        using var console = ConsoleOutput.StartFullCapture();

        var run = Task.Run(() => commands.Refresh(stabilize: s => s));
        await acked.Task.WaitAsync(TimeSpan.FromSeconds(30));
        for (var steps = 0; !run.IsCompleted; steps++) {
            if (steps > 2000) throw new TimeoutException("the refresh never gave up waiting under the fake clock");
            time.Advance(TimeSpan.FromMilliseconds(500));
            await Task.Delay(5);
        }

        await Assert.That(await run).IsEqualTo(0);
        await Assert.That(calls.Count(c => c[0] == "bootout")).IsEqualTo(1);
        await Assert.That(calls.Count(c => c[0] == "bootstrap")).IsEqualTo(1);
        await Assert.That(console.GetCapturedOutput()).Contains("reloaded with its updated service unit");
        await Assert.That(console.GetCapturedOutput()).Contains("ended by the reload");
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
