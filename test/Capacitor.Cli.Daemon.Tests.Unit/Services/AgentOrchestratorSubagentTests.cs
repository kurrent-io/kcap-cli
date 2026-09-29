using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// The orchestrator's half of the subagent count: a relayed report lands on the attributed agent's
/// clock, the published count follows the agent's status, and one shared timer retires aged ids so
/// that every expiry is announced exactly once, whichever call meets it. A FakeTimeProvider fires
/// the timer as time advances; a ManualTimerTimeProvider holds the callback until the test runs it.
public class AgentOrchestratorSubagentTests {
    static readonly TimeSpan Liveness = AgentActivityClock.SubagentLiveness;

    static AgentOrchestrator Build(TimeProvider time) =>
        AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(), timeProvider: time);

    static long Ms(TimeProvider time) => time.GetUtcNow().ToUnixTimeMilliseconds();

    static int? Published(AgentOrchestrator orch, string agentId) =>
        orch.SnapshotAgentsForStatus().Single(a => a.Id == agentId).LiveSubagents;

    [Test]
    public async Task A_relayed_report_reaches_the_attributed_agents_clock_and_pulses_on_a_changed_value() {
        var time = new FakeTimeProvider();
        await using var orch  = Build(time);
        var             agent = orch.SeedAgentForTest("sub-1");
        var             relay = orch.PermissionBridgeForTest.SubagentHandler!;
        var             v0    = orch.StatusNotifierForTest.Version;

        relay(agent.Id, "child-a", true, Ms(time));

        await Assert.That(agent.ActivityClock.LiveSubagents).IsEqualTo(1);
        await Assert.That(orch.StatusNotifierForTest.Version).IsGreaterThan(v0);

        var v1 = orch.StatusNotifierForTest.Version;
        relay(agent.Id, "child-a", true, Ms(time));

        await Assert.That(agent.ActivityClock.LiveSubagents).IsEqualTo(1);
        await Assert.That(orch.StatusNotifierForTest.Version).IsEqualTo(v1);

        relay("somebody-else", "child-b", true, Ms(time));
        await Assert.That(agent.ActivityClock.LiveSubagents).IsEqualTo(1);
    }

    [Test]
    public async Task An_unchanged_report_pulses_only_when_its_call_retired_an_expired_id() {
        var time = new ManualTimerTimeProvider();
        await using var orch  = Build(time);
        var             agent = orch.SeedAgentForTest("sub-2");
        var             relay = orch.PermissionBridgeForTest.SubagentHandler!;

        relay(agent.Id, "child-a", true, Ms(time));
        time.Advance(TimeSpan.FromMinutes(5));
        relay(agent.Id, "child-b", true, Ms(time));
        time.Advance(TimeSpan.FromMinutes(5));
        await Assert.That(Published(orch, agent.Id)).IsEqualTo(2);

        var v0 = orch.StatusNotifierForTest.Version;
        relay(agent.Id, "child-b", true, Ms(time));

        await Assert.That(orch.StatusNotifierForTest.Version).IsEqualTo(v0 + 1);
        await Assert.That(Published(orch, agent.Id)).IsEqualTo(1);

        var v1 = orch.StatusNotifierForTest.Version;
        relay(agent.Id, "child-b", true, Ms(time));
        await Assert.That(orch.StatusNotifierForTest.Version).IsEqualTo(v1);
    }

    /// A live report says "alive now", so an old one says nothing; a stop is a fact whatever its
    /// age. That the stop reached the clock shows in the restart window it opens.
    [Test]
    public async Task A_live_report_more_than_60_seconds_old_never_reaches_the_clock_and_a_stop_that_old_still_does() {
        var time = new FakeTimeProvider();
        await using var orch  = Build(time);
        var             agent = orch.SeedAgentForTest("sub-3");
        var             relay = orch.PermissionBridgeForTest.SubagentHandler!;

        relay(agent.Id, "child-a", true, Ms(time) - 60_001);
        await Assert.That(agent.ActivityClock.LiveSubagents).IsNull();

        relay(agent.Id, "child-a", true, Ms(time) - 60_000);
        await Assert.That(agent.ActivityClock.LiveSubagents).IsEqualTo(1);

        relay(agent.Id, "child-b", false, Ms(time) - 61_000);
        relay(agent.Id, "child-b", true, Ms(time) - 40_000);
        await Assert.That(agent.ActivityClock.LiveSubagents).IsEqualTo(1);
    }

    [Test]
    public async Task The_published_count_is_null_before_any_report_the_clocks_count_while_running_and_zero_otherwise() {
        var time = new FakeTimeProvider();
        await using var orch     = Build(time);
        var             running  = orch.SeedAgentForTest("run");
        var             starting = orch.SeedAgentForTest("start", status: "Starting");
        var             done     = orch.SeedAgentForTest("done", status: "Completed");
        var             relay    = orch.PermissionBridgeForTest.SubagentHandler!;

        foreach (var id in (string[]) ["run", "start", "done"]) await Assert.That(Published(orch, id)).IsNull();

        relay(running.Id, "child", true, Ms(time));
        relay(starting.Id, "child", true, Ms(time));
        relay(done.Id, "child", true, Ms(time));

        await Assert.That(Published(orch, "run")).IsEqualTo(1);
        await Assert.That(Published(orch, "start")).IsEqualTo(0);
        await Assert.That(Published(orch, "done")).IsEqualTo(0);
        await Assert.That(starting.ActivityClock.LiveSubagents).IsEqualTo(1);
    }

    [Test]
    public async Task The_timer_pulses_at_the_expiry_and_re_arms_for_the_next() {
        var time = new FakeTimeProvider();
        await using var orch  = Build(time);
        var             agent = orch.SeedAgentForTest("sub-5");
        var             relay = orch.PermissionBridgeForTest.SubagentHandler!;

        relay(agent.Id, "child-a", true, Ms(time));
        time.Advance(TimeSpan.FromMinutes(1));
        relay(agent.Id, "child-b", true, Ms(time));
        var v0 = orch.StatusNotifierForTest.Version;

        time.Advance(TimeSpan.FromMinutes(9));
        await Assert.That(Published(orch, agent.Id)).IsEqualTo(1);
        await Assert.That(orch.StatusNotifierForTest.Version).IsEqualTo(v0 + 1);

        time.Advance(TimeSpan.FromMinutes(1));
        await Assert.That(Published(orch, agent.Id)).IsEqualTo(0);
        await Assert.That(orch.StatusNotifierForTest.Version).IsEqualTo(v0 + 2);
    }

    [Test]
    public async Task A_callback_held_past_the_second_deadline_publishes_a_count_of_neither() {
        var time = new ManualTimerTimeProvider();
        await using var orch  = Build(time);
        var             agent = orch.SeedAgentForTest("sub-6");
        var             relay = orch.PermissionBridgeForTest.SubagentHandler!;

        relay(agent.Id, "child-a", true, Ms(time));
        time.Advance(TimeSpan.FromMinutes(1));
        relay(agent.Id, "child-b", true, Ms(time));
        await Assert.That(time.Timer!.Due).IsEqualTo(TimeSpan.FromMinutes(9));

        time.Advance(TimeSpan.FromMinutes(10));
        await Assert.That(Published(orch, agent.Id)).IsEqualTo(2);
        var v0 = orch.StatusNotifierForTest.Version;

        time.Timer.Fire();

        await Assert.That(Published(orch, agent.Id)).IsEqualTo(0);
        await Assert.That(orch.StatusNotifierForTest.Version).IsEqualTo(v0 + 1);
        await Assert.That(time.Timer.Due).IsEqualTo(Timeout.InfiniteTimeSpan);
    }

    [Test]
    public async Task Another_sessions_unchanged_heartbeat_publishes_a_zero_whose_deadline_passed_before_the_call_began() {
        var time = new ManualTimerTimeProvider();
        await using var orch  = Build(time);
        var             a     = orch.SeedAgentForTest("a");
        var             b     = orch.SeedAgentForTest("b");
        var             relay = orch.PermissionBridgeForTest.SubagentHandler!;

        relay(a.Id, "child-a", true, Ms(time));
        relay(b.Id, "child-b", true, Ms(time));
        time.Advance(TimeSpan.FromMinutes(5));
        relay(b.Id, "child-b", true, Ms(time));
        time.Advance(TimeSpan.FromMinutes(5));
        await Assert.That(Published(orch, a.Id)).IsEqualTo(1);
        var v0 = orch.StatusNotifierForTest.Version;

        relay(b.Id, "child-b", true, Ms(time));

        await Assert.That(Published(orch, a.Id)).IsEqualTo(0);
        await Assert.That(Published(orch, b.Id)).IsEqualTo(1);
        await Assert.That(orch.StatusNotifierForTest.Version).IsEqualTo(v0 + 1);

        for (var i = 0; i < 3; i++) {
            time.Advance(TimeSpan.FromMinutes(1));
            relay(b.Id, "child-b", true, Ms(time));
            await Assert.That(Published(orch, a.Id)).IsEqualTo(0);
            await Assert.That(a.ActivityClock.LiveSubagents).IsEqualTo(0);
        }
    }

    [Test]
    public async Task Another_sessions_unchanged_heartbeat_publishes_a_zero_whose_deadline_passes_inside_the_call() {
        var time = new ManualTimerTimeProvider();
        await using var orch  = Build(time);
        var             a     = orch.SeedAgentForTest("a");
        var             b     = orch.SeedAgentForTest("b");
        var             relay = orch.PermissionBridgeForTest.SubagentHandler!;

        relay(a.Id, "child-a", true, Ms(time));
        relay(b.Id, "child-b", true, Ms(time));
        time.Advance(TimeSpan.FromMinutes(5));
        relay(b.Id, "child-b", true, Ms(time));
        time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        var v0 = orch.StatusNotifierForTest.Version;
        orch.BeforeSubagentSweepForTest = () => time.Advance(TimeSpan.FromSeconds(1));

        relay(b.Id, "child-b", true, Ms(time));

        await Assert.That(Published(orch, a.Id)).IsEqualTo(0);
        await Assert.That(orch.StatusNotifierForTest.Version).IsEqualTo(v0 + 1);
    }

    [Test]
    public async Task With_the_callback_held_the_subagents_stop_alone_publishes_zero_and_the_released_callback_adds_nothing() {
        var time = new ManualTimerTimeProvider();
        await using var orch  = Build(time);
        var             agent = orch.SeedAgentForTest("sub-9");
        var             relay = orch.PermissionBridgeForTest.SubagentHandler!;

        relay(agent.Id, "child-a", true, Ms(time));
        await Assert.That(Published(orch, agent.Id)).IsEqualTo(1);
        time.Advance(TimeSpan.FromMinutes(11));
        await Assert.That(Published(orch, agent.Id)).IsEqualTo(1);
        var v0 = orch.StatusNotifierForTest.Version;

        relay(agent.Id, "child-a", false, Ms(time));

        await Assert.That(Published(orch, agent.Id)).IsEqualTo(0);
        await Assert.That(orch.StatusNotifierForTest.Version).IsEqualTo(v0 + 1);

        time.Timer!.Fire();
        await Assert.That(orch.StatusNotifierForTest.Version).IsEqualTo(v0 + 1);
    }

    /// The report runs inside the callback's hold, ahead of its sweep: its own call arms the timer
    /// for the new id, and the callback's later sweep finds that deadline already set. The relay
    /// call here re-enters `_subagentExpiryLock` on the same thread as the callback holding it;
    /// production reaches the same end state from another thread, which blocks on the lock instead.
    [Test]
    public async Task A_report_that_lands_while_the_callback_is_rescheduling_still_has_its_expiry_fire() {
        var time = new ManualTimerTimeProvider();
        await using var orch  = Build(time);
        var             agent = orch.SeedAgentForTest("sub-10");
        var             relay = orch.PermissionBridgeForTest.SubagentHandler!;

        relay(agent.Id, "child-a", true, Ms(time));
        time.Advance(Liveness);
        orch.BeforeSubagentSweepForTest = () => {
            orch.BeforeSubagentSweepForTest = null;
            relay(agent.Id, "child-b", true, Ms(time));
        };

        time.Timer!.Fire();

        await Assert.That(Published(orch, agent.Id)).IsEqualTo(1);
        await Assert.That(time.Timer.Due).IsEqualTo(Liveness);

        time.Advance(Liveness);
        var v0 = orch.StatusNotifierForTest.Version;
        time.Timer.Fire();

        await Assert.That(Published(orch, agent.Id)).IsEqualTo(0);
        await Assert.That(orch.StatusNotifierForTest.Version).IsEqualTo(v0 + 1);
    }

    [Test]
    public async Task A_report_taken_while_starting_expires_on_time_once_the_agent_runs() {
        var time = new FakeTimeProvider();
        await using var orch  = Build(time);
        var             agent = orch.SeedAgentForTest("sub-11", status: "Starting");
        var             relay = orch.PermissionBridgeForTest.SubagentHandler!;

        relay(agent.Id, "child-a", true, Ms(time));
        await Assert.That(Published(orch, agent.Id)).IsEqualTo(0);

        time.Advance(TimeSpan.FromMinutes(5));
        orch.SetAgentStatus(agent, "Running");
        await Assert.That(Published(orch, agent.Id)).IsEqualTo(1);

        var v0 = orch.StatusNotifierForTest.Version;
        time.Advance(TimeSpan.FromMinutes(5));
        await Assert.That(Published(orch, agent.Id)).IsEqualTo(0);
        await Assert.That(orch.StatusNotifierForTest.Version).IsEqualTo(v0 + 1);
    }

    [Test]
    public async Task A_callback_held_across_disposal_publishes_nothing_and_nothing_re_arms() {
        var time  = new ManualTimerTimeProvider();
        var orch  = Build(time);
        var agent = orch.SeedAgentForTest("sub-12");
        orch.PermissionBridgeForTest.SubagentHandler!(agent.Id, "child-a", true, Ms(time));
        time.Advance(TimeSpan.FromMinutes(11));

        await orch.DisposeAsync();
        await Assert.That(time.Timer!.Disposed).IsTrue();
        var v0      = orch.StatusNotifierForTest.Version;
        var changes = time.Timer.Changes;

        time.Timer.Fire();

        await Assert.That(orch.StatusNotifierForTest.Version).IsEqualTo(v0);
        await Assert.That(time.Timer.Changes).IsEqualTo(changes);
    }
}
