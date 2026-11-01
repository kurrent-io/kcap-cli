using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class RestartCoordinatorTests {
    // Per-test isolation for the restart marker the coordinator writes.
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    sealed class SpyStrategy : IRestartStrategy {
        public int            Calls;
        public RestartOutcome Outcome = RestartOutcome.Initiated;
        public RestartOutcome Restart() { Calls++; return Outcome; }
    }

    RestartCoordinator NewCoordinator(IRestartStrategy spy, Func<bool> isBusy, Func<BinaryStat?> stat) {
        var c = RestartCoordinator.ForTest(Daemons.Store, "laptop", "v0.4.11", spy, TimeProvider.System);
        c.IsBusy     = isBusy;
        c.StatBinary = stat;
        c.PrimeBaseline();   // capture initial stat as baseline
        return c;
    }

    [Test]
    public async Task Binary_change_while_idle_triggers_restart() {
        var spy   = new SpyStrategy();
        var size  = 100L;
        var c     = NewCoordinator(spy, isBusy: () => false, stat: () => new BinaryStat(size, 1));
        size = 200; // simulate update landing
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task Binary_change_while_busy_waits_then_fires_when_idle() {
        var spy  = new SpyStrategy();
        var size = 100L;
        var busy = true;
        var c    = NewCoordinator(spy, isBusy: () => busy, stat: () => new BinaryStat(size, 1));
        size = 200;
        c.Tick();                                   // busy → queued, no fire
        await Assert.That(spy.Calls).IsEqualTo(0);
        busy = false;
        c.Tick();                                   // idle → fire
        await Assert.That(spy.Calls).IsEqualTo(1);
    }

    /// An update can replace the binary while the daemon is still booting, before the poll primes its baseline.
    [Test]
    public async Task A_binary_replaced_before_the_poll_starts_still_restarts() {
        var spy = new SpyStrategy();
        var c   = RestartCoordinator.ForTest(Daemons.Store, "laptop", "v0.4.11", spy, TimeProvider.System);
        c.IsBusy       = () => false;
        c.StatBinary   = () => new BinaryStat(200, 2);
        c.LaunchBinary = new BinaryStat(100, 1);
        c.PrimeBaseline();
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task Transient_stat_failure_does_not_queue() {
        var spy = new SpyStrategy();
        var c   = NewCoordinator(spy, isBusy: () => false, stat: () => null);
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(0);
    }

    [Test]
    public async Task Explicit_force_request_fires_even_when_busy() {
        var spy = new SpyStrategy();
        var c   = NewCoordinator(spy, isBusy: () => true, stat: () => new BinaryStat(100, 1));
        c.RequestRestart(force: true);
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task Restart_only_fires_once() {
        var spy  = new SpyStrategy();
        var size = 100L;
        var c    = NewCoordinator(spy, isBusy: () => false, stat: () => new BinaryStat(size, 1));
        size = 200;
        c.Tick();
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task Failed_restart_is_retried_on_next_tick() {
        var spy  = new SpyStrategy { Outcome = RestartOutcome.Retry };
        var size = 100L;
        var c    = NewCoordinator(spy, isBusy: () => false, stat: () => new BinaryStat(size, 1));
        size = 200;
        c.Tick();                                   // fires, strategy returns Retry → un-claimed
        await Assert.That(spy.Calls).IsEqualTo(1);
        c.Tick();                                   // still pending → retries
        await Assert.That(spy.Calls).IsEqualTo(2);
    }

    [Test]
    public async Task Noop_outcome_is_terminal() {
        var spy  = new SpyStrategy { Outcome = RestartOutcome.NoOp };
        var size = 100L;
        var c    = NewCoordinator(spy, isBusy: () => false, stat: () => new BinaryStat(size, 1));
        size = 200;
        c.Tick();
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(1);  // NoOp is terminal — no retry, no spam
    }

    [Test]
    public async Task Request_restart_fires_immediately_when_idle() {
        var spy = new SpyStrategy();
        var c   = NewCoordinator(spy, isBusy: () => false, stat: () => new BinaryStat(100, 1));

        var result = c.RequestRestart(force: false);   // no Tick needed — evaluates now

        await Assert.That(spy.Calls).IsEqualTo(1);
        await Assert.That(result).IsEqualTo(RestartRequestResult.Restarting);
    }

    [Test]
    public async Task Request_restart_when_busy_queues_then_fires_when_idle() {
        var spy  = new SpyStrategy();
        var busy = true;
        var c    = NewCoordinator(spy, isBusy: () => busy, stat: () => new BinaryStat(100, 1));

        var result = c.RequestRestart(force: false);
        await Assert.That(result).IsEqualTo(RestartRequestResult.Queued);
        await Assert.That(spy.Calls).IsEqualTo(0);

        busy = false;
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(1);
    }

    /// <summary>On Windows only a script install is watched: an update switches its <c>current</c>, so the
    /// binary reached through it changes while the file the daemon runs from does not.</summary>
    [Test]
    public async Task On_Windows_a_script_install_is_watched_through_current() {
        using var tmp = new TempDir();
        tmp.CreateFile("kcap/install.json", "{}");
        var running = tmp.CreateFile("kcap/versions/1.0.0/bin/kcap-daemon", "old");
        tmp.CreateFile("kcap/current/bin/kcap-daemon", "old");

        var before = RestartCoordinator.StatBinaryAt(running, isWindows: true, File.Exists);
        tmp.CreateFile("kcap/current/bin/kcap-daemon", "the new version");
        var after = RestartCoordinator.StatBinaryAt(running, isWindows: true, File.Exists);

        await Assert.That(before).IsNotNull();
        await Assert.That(RestartDecision.BinaryChanged(before, after)).IsTrue();
        await Assert.That(File.ReadAllText(running)).IsEqualTo("old");
    }

    /// <summary>Two releases' binaries can match on size and mtime, so the switch of <c>current</c> itself
    /// must register as the change.</summary>
    [Test]
    public async Task Switching_current_between_identical_binaries_is_a_change() {
        Skip.When(OperatingSystem.IsWindows(), "the fixture's current is a symlink; Windows uses a junction");
        using var tmp = new TempDir();
        tmp.CreateFile("kcap/install.json", "{}");
        var mtime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        foreach (var v in new[] { "1.0.0", "1.1.0" })
            File.SetLastWriteTimeUtc(tmp.CreateFile($"kcap/versions/{v}/bin/kcap-daemon", "same"), mtime);
        var current = tmp.PathTo("kcap/current");
        Directory.CreateSymbolicLink(current, "versions/1.0.0");
        var running = tmp.PathTo("kcap/versions/1.0.0/bin/kcap-daemon");

        var before = RestartCoordinator.StatBinaryAt(running, isWindows: false, File.Exists);
        Directory.Delete(current);
        Directory.CreateSymbolicLink(current, "versions/1.1.0");
        var after = RestartCoordinator.StatBinaryAt(running, isWindows: false, File.Exists);

        await Assert.That(before).IsNotNull();
        await Assert.That(RestartDecision.BinaryChanged(before, after)).IsTrue();
    }

    [Test]
    public async Task On_Windows_a_binary_outside_a_script_install_is_not_watched() {
        using var tmp = new TempDir();
        var exe = tmp.CreateFile("npm/bin/kcap-daemon", "x");

        await Assert.That(RestartCoordinator.StatBinaryAt(exe, isWindows: true, File.Exists)).IsNull();
        await Assert.That(RestartCoordinator.StatBinaryAt(exe, isWindows: false, File.Exists)).IsNotNull();
    }

    [Test]
    public async Task Binary_change_with_agents_mid_turn_waits_until_the_boundary() {
        var spy      = new SpyStrategy();
        var size     = 100L;
        var boundary = false;
        var c        = NewCoordinator(spy, isBusy: () => false, stat: () => new BinaryStat(size, 1));
        c.AtTurnBoundary = () => boundary;
        size = 200;
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(0);
        boundary = true;
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task Binary_change_fires_at_the_bound_without_a_boundary() {
        var spy  = new SpyStrategy();
        var time = new FakeTimeProvider();
        var size = 100L;
        var c    = RestartCoordinator.ForTest(Daemons.Store, "laptop", "v0.4.11", spy, time);
        c.IsBusy         = () => false;
        c.AtTurnBoundary = () => false;
        c.TurnWait       = TimeSpan.FromMinutes(5);
        c.StatBinary     = () => new BinaryStat(size, 1);
        c.PrimeBaseline();
        size = 200;
        c.Tick();
        time.Advance(TimeSpan.FromMinutes(4));
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(0);
        time.Advance(TimeSpan.FromMinutes(1));
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(1);
    }

    /// <summary>A mid-round flow participant holds a self-detected restart only until the wait bound;
    /// an eval in flight still holds it past the bound.</summary>
    [Test]
    public async Task A_mid_round_flow_participant_holds_a_self_detected_restart_only_until_the_bound() {
        var spy  = new SpyStrategy();
        var time = new FakeTimeProvider();
        var size = 100L;
        var hard = false;
        var c    = RestartCoordinator.ForTest(Daemons.Store, "laptop", "v0.4.11", spy, time);
        c.IsHardBusy     = () => hard;
        c.IsBusy         = () => true;
        c.AtTurnBoundary = () => true;
        c.TurnWait       = TimeSpan.FromMinutes(5);
        c.StatBinary     = () => new BinaryStat(size, 1);
        c.PrimeBaseline();
        size = 200;
        c.Tick();
        time.Advance(TimeSpan.FromMinutes(4));
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(0);

        hard = true;
        time.Advance(TimeSpan.FromMinutes(1));
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(0);

        hard = false;
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(1);
    }

    RestartCoordinator WiredCoordinator(SpyStrategy spy, AgentOrchestrator orch) =>
        RestartCoordinator.ForTest(
            Daemons.Store, "laptop", "v0.4.11", spy, TimeProvider.System, orch, new EvalContextCache(TimeProvider.System));

    static AgentOrchestrator BuildOrchestrator() =>
        AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

    [Test]
    public async Task Running_interactive_agents_do_not_block_an_explicit_restart() {
        var spy = new SpyStrategy();
        await using var orch = BuildOrchestrator();
        orch.SeedAgentForTest("interactive-1", status: "Running").ActivityClock.SetTurnInFlight(true);
        var c = WiredCoordinator(spy, orch);

        await Assert.That(orch.InteractiveAgentsAtTurnBoundary).IsFalse();
        c.RequestRestart(force: false);

        await Assert.That(spy.Calls).IsEqualTo(1);
    }

    /// <summary>A when-idle request waits for no agents at all, mid-turn or not, while a now request
    /// proceeds with the agent running.</summary>
    [Test]
    public async Task A_when_idle_request_waits_for_a_running_interactive_agent_and_now_does_not() {
        var spy = new SpyStrategy();
        await using var orch = BuildOrchestrator();
        orch.SeedAgentForTest("interactive-1", status: "Running").ActivityClock.SetTurnInFlight(true);
        var c = WiredCoordinator(spy, orch);

        await Assert.That(c.RequestRestart(force: false, whenIdle: true)).IsEqualTo(RestartRequestResult.Queued);
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(0);

        await Assert.That(c.RequestRestart(force: false)).IsEqualTo(RestartRequestResult.Restarting);
        await Assert.That(spy.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task A_when_idle_request_fires_once_the_last_agent_is_gone() {
        var spy    = new SpyStrategy();
        var agents = true;
        var c      = NewCoordinator(spy, isBusy: () => false, stat: () => new BinaryStat(100, 1));
        c.HasAgents = () => agents;

        c.RequestRestart(force: false, whenIdle: true);
        await Assert.That(spy.Calls).IsEqualTo(0);
        agents = false;
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task A_mid_round_flow_participant_blocks_an_explicit_restart_but_not_a_forced_one() {
        var spy = new SpyStrategy();
        await using var orch = BuildOrchestrator();
        orch.SeedAgentForTest("flow-1", LaunchKind.ReviewFlow, status: "Running", flowRunId: "run-1", flowRole: "reviewer")
            .ActivityClock.SetTurnInFlight(true);
        var c = WiredCoordinator(spy, orch);

        await Assert.That(c.RequestRestart(force: false)).IsEqualTo(RestartRequestResult.Queued);
        await Assert.That(spy.Calls).IsEqualTo(0);

        c.RequestRestart(force: true);
        await Assert.That(spy.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task Force_ignores_busy_flow_participants_but_not_evals() {
        var spy  = new SpyStrategy();
        var hard = true;
        var c    = NewCoordinator(spy, isBusy: () => true, stat: () => new BinaryStat(100, 1));
        c.IsHardBusy = () => hard;
        c.RequestRestart(force: true);
        await Assert.That(spy.Calls).IsEqualTo(0);
        hard = false;
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task A_harness_installed_after_startup_restarts_once_no_agents_remain() {
        var spy       = new SpyStrategy();
        var installed = false;
        var agents    = true;
        var c         = NewCoordinator(spy, isBusy: () => false, stat: () => new BinaryStat(100, 1));
        c.HasAgents        = () => agents;
        c.HarnessInstalled = () => installed;

        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(0);

        installed = true;
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(0);

        agents = false;
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(1);
    }

    /// <summary>An update needs only a turn boundary, so a harness restart already waiting for every
    /// agent to finish must not hold it back.</summary>
    [Test]
    public async Task An_update_landing_behind_a_harness_restart_fires_at_the_turn_boundary() {
        var spy  = new SpyStrategy();
        var size = 100L;
        var c    = NewCoordinator(spy, isBusy: () => false, stat: () => new BinaryStat(size, 1));
        c.HasAgents        = () => true;
        c.HarnessInstalled = () => true;

        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(0);

        size = 200;
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(1);
    }

    [Test]
    public async Task Only_an_unadvertised_vendor_that_now_finds_its_CLI_counts_as_installed() {
        IHostedAgentRuntimeFactory[] advertisedOnly = [new FakeFactory("claude", available: true)];
        IHostedAgentRuntimeFactory[] stillMissing   = [new FakeFactory("claude", available: true), new FakeFactory("codex", available: false)];
        IHostedAgentRuntimeFactory[] nowInstalled   = [new FakeFactory("claude", available: true), new FakeFactory("codex", available: true)];

        await Assert.That(RestartCoordinator.UnadvertisedNowAvailable(["claude"], advertisedOnly)).IsFalse();
        await Assert.That(RestartCoordinator.UnadvertisedNowAvailable(["claude"], stillMissing)).IsFalse();
        await Assert.That(RestartCoordinator.UnadvertisedNowAvailable(["claude"], nowInstalled)).IsTrue();
        await Assert.That(RestartCoordinator.UnadvertisedNowAvailable([], nowInstalled)).IsTrue();
        await Assert.That(RestartCoordinator.UnadvertisedNowAvailable(null, nowInstalled)).IsFalse();
    }

    sealed class FakeFactory(string vendor, bool available) : IHostedAgentRuntimeFactory {
        public string CliPath            => vendor;
        public string Vendor             => vendor;
        public bool   SupportsUnattended => false;
        public bool   IsAvailable()      => available;

        public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    /// <summary>A foreground daemon answers a harness restart with a no-op; an update after it must still
    /// be detected rather than swallowed by a coordinator left fired.</summary>
    [Test]
    public async Task A_no_op_harness_restart_leaves_update_detection_working() {
        var spy  = new SpyStrategy { Outcome = RestartOutcome.NoOp };
        var size = 100L;
        var c    = NewCoordinator(spy, isBusy: () => false, stat: () => new BinaryStat(size, 1));
        c.HarnessInstalled = () => true;

        c.Tick();
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(1);

        size = 200;
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(2);
    }

    /// <summary>A when-idle request must not turn a pending update into one that waits for every agent.</summary>
    [Test]
    public async Task A_when_idle_request_keeps_a_pending_update_on_its_turn_boundary() {
        var spy      = new SpyStrategy();
        var size     = 100L;
        var boundary = false;
        var c        = NewCoordinator(spy, isBusy: () => false, stat: () => new BinaryStat(size, 1));
        c.HasAgents      = () => true;
        c.AtTurnBoundary = () => boundary;

        size = 200;
        c.Tick();
        c.RequestRestart(force: false, whenIdle: true);
        await Assert.That(spy.Calls).IsEqualTo(0);

        boundary = true;
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(1);
    }

    /// <summary>A when-idle request that reaches the daemon before its poll sees the new binary must not
    /// hide the update: idle agents never leave, so only the turn-boundary restart would ever fire.</summary>
    [Test]
    public async Task An_update_landing_behind_a_when_idle_request_fires_at_the_turn_boundary() {
        var spy  = new SpyStrategy();
        var size = 100L;
        var c    = NewCoordinator(spy, isBusy: () => false, stat: () => new BinaryStat(size, 1));
        c.HasAgents = () => true;

        c.RequestRestart(force: false, whenIdle: true);
        await Assert.That(spy.Calls).IsEqualTo(0);

        size = 200;
        c.Tick();
        await Assert.That(spy.Calls).IsEqualTo(1);
        await Assert.That(DaemonRestartMarker.TryRead(Daemons.Store, "laptop")!.Reason).IsEqualTo("self-detected");
    }

    /// <summary>A request that lands while a harness restart is running stays pending after its no-op.</summary>
    [Test]
    public async Task A_request_during_a_no_op_harness_restart_is_not_dropped() {
        var c   = (RestartCoordinator?)null;
        var spy = new ReentrantStrategy(() => c!.RequestRestart(force: false));
        c = NewCoordinator(spy, isBusy: () => false, stat: () => new BinaryStat(100, 1));
        c.HarnessInstalled = () => true;

        c.Tick();
        c.Tick();

        await Assert.That(spy.Calls).IsEqualTo(2);
    }

    sealed class ReentrantStrategy(Action duringFirst) : IRestartStrategy {
        public int Calls;
        public RestartOutcome Restart() {
            if (++Calls == 1) duringFirst();
            return RestartOutcome.NoOp;
        }
    }

    /// <summary>A snap refresh repoints <c>current</c> at the new revision; that, not the binary's size or
    /// timestamp, is what tells the revisions apart.</summary>
    [Test]
    public async Task A_snap_binary_is_compared_by_the_revision_current_names() {
        var asked = new List<string>();
        string? Link(string dir) { asked.Add(dir); return "43"; }

        var target = RestartCoordinator.CurrentTarget("/snap/kcap/42/bin/kcap-daemon", layout: null, Link);

        await Assert.That(target).IsEqualTo("43");
        await Assert.That(asked).IsEquivalentTo(["/snap/kcap/current"]);
    }

    [Test]
    public async Task Outside_a_script_install_or_a_snap_there_is_no_link_to_compare() {
        await Assert.That(RestartCoordinator.CurrentTarget("/usr/local/bin/kcap-daemon", layout: null, _ => "unexpected")).IsNull();
    }
}
