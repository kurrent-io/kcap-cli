using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>The reason a daemon reports when it unregisters an agent: what a flow driver reads beside
/// <c>participant_died</c>, so a stop by hand or a reap reads apart from a crash.</summary>
public class AgentStopReasonCodeTests {
    [Test]
    [Arguments("agent_exited", null)]
    [Arguments("agent_stopped", "agent_stopped")]
    [Arguments("reviewer_ttl_expired", "reviewer_ttl_expired")]
    public async Task An_agent_with_no_verdict_reports_the_end_reason_a_stop_or_reap_stamped(string endReason, string? expected) {
        var agent = new AgentInstance(
            "a1", "p", "m", null, "/repo", "codex", new FakeHostedAgentRuntime("codex", emitsTerminalOutput: false),
            new WorktreeInfo("/repo", "b", "/repo"), new CancellationTokenSource()) {
            ActivityClock = new AgentActivityClock(TimeProvider.System), CreatedAt = DateTime.UtcNow, LastOutputAt = DateTime.UtcNow,
            PendingEndReason = endReason
        };

        await Assert.That(AgentOrchestrator.StopReasonCode(agent)).IsEqualTo(expected);
    }
}
