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

    RestartCoordinator NewCoordinator(SpyStrategy spy, Func<bool> isBusy, Func<BinaryStat?> stat) {
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
}
