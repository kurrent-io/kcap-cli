using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>The reason a daemon reports when it unregisters an agent: what a flow driver reads beside
/// <c>participant_died</c>, so a stop by hand or a reap reads apart from a crash.</summary>
public class AgentStopReasonCodeTests {
    /// A daemon's own stop names itself, so a driver does not read it as a person's.
    [Test]
    [Arguments("agent_exited", null, null)]
    [Arguments("agent_stopped", null, "agent_stopped")]
    [Arguments("reviewer_ttl_expired", null, "reviewer_ttl_expired")]
    [Arguments("agent_stopped", "agent_startup_timeout", "agent_startup_timeout")]
    public async Task An_agent_with_no_verdict_reports_the_end_reason_a_stop_or_reap_stamped(string endReason, string? stopDetail, string? expected) {
        var agent = new AgentInstance(
            "a1", "p", "m", null, "/repo", "codex", new FakeHostedAgentRuntime("codex", emitsTerminalOutput: false),
            new WorktreeInfo("/repo", "b", "/repo"), new CancellationTokenSource()) {
            ActivityClock = new AgentActivityClock(TimeProvider.System), CreatedAt = DateTime.UtcNow, LastOutputAt = DateTime.UtcNow,
            PendingEndReason = endReason, StopDetail = stopDetail
        };

        await Assert.That(AgentOrchestrator.StopReasonCode(agent)).IsEqualTo(expected);
    }

    /// A hand stop keeps its detail even when a late park acknowledgement resets the end reason.
    [Test]
    public async Task A_hand_stop_reports_agent_stopped_through_a_reset_end_reason() {
        var server = new CaptureServerConnection();
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        var agent = orch.SeedAgentForTest("hand-stop", status: "Running");

        await orch.HandleStopAgentForTest("hand-stop");
        agent.PendingEndReason = "agent_exited";

        await Assert.That(AgentOrchestrator.StopReasonCode(agent)).IsEqualTo("agent_stopped");
    }
}
