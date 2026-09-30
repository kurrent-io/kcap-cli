using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// A running PTY agent whose terminal has gone quiet is waiting, including when the stop notice
/// never stuck. The published status is what the desktop badge reads.
public class AgentQuietTurnTests {
    static AgentOrchestrator Build(TimeProvider time) =>
        AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(), timeProvider: time);

    static bool? Awaiting(AgentOrchestrator orch, string id) =>
        orch.SnapshotAgentsForStatus().Single(a => a.Id == id).AwaitingInput;

    [Test]
    public async Task A_quiet_pty_agent_is_published_as_waiting_and_output_clears_it() {
        var time = new FakeTimeProvider();
        await using var orch  = Build(time);
        var             agent = orch.SeedAgentForTest("quiet-1");

        time.Advance(AgentActivityClock.QuietTurn - TimeSpan.FromSeconds(1));
        await Assert.That(Awaiting(orch, agent.Id)).IsFalse();

        time.Advance(TimeSpan.FromSeconds(1));
        await Assert.That(Awaiting(orch, agent.Id)).IsTrue();

        agent.ActivityClock.Advance();
        await Assert.That(Awaiting(orch, agent.Id)).IsFalse();
    }

    [Test]
    public async Task A_live_subagent_keeps_the_parent_from_being_marked_quiet() {
        var time = new FakeTimeProvider();
        await using var orch  = Build(time);
        var             agent = orch.SeedAgentForTest("quiet-2");
        orch.PermissionBridgeForTest.SubagentHandler!(agent.Id, "child", true, time.GetUtcNow().ToUnixTimeMilliseconds());

        time.Advance(AgentActivityClock.QuietTurn);
        await Assert.That(Awaiting(orch, agent.Id)).IsFalse();
    }

    [Test]
    public async Task A_subagent_stop_marks_a_parent_whose_terminal_is_already_quiet() {
        var time = new FakeTimeProvider();
        await using var orch  = Build(time);
        var             agent = orch.SeedAgentForTest("quiet-3");
        orch.PermissionBridgeForTest.SubagentHandler!(agent.Id, "child", true, time.GetUtcNow().ToUnixTimeMilliseconds());

        time.Advance(AgentActivityClock.QuietTurn);
        orch.PermissionBridgeForTest.SubagentHandler!(agent.Id, "child", false, time.GetUtcNow().ToUnixTimeMilliseconds());

        await Assert.That(Awaiting(orch, agent.Id)).IsTrue();
    }
}
