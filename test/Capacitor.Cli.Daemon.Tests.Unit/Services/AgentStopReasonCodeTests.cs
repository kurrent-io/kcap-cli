using Capacitor.Cli.Daemon.Services;

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
    public async Task An_agent_with_no_verdict_reports_the_end_reason_a_stop_or_reap_stamped(string endReason, string? autoStop, string? expected) {
        var agent = new AgentInstance(
            "a1", "p", "m", null, "/repo", "codex", new FakeHostedAgentRuntime("codex", emitsTerminalOutput: false),
            new WorktreeInfo("/repo", "b", "/repo"), new CancellationTokenSource()) {
            ActivityClock = new AgentActivityClock(TimeProvider.System), CreatedAt = DateTime.UtcNow, LastOutputAt = DateTime.UtcNow,
            PendingEndReason = endReason, AutoStopReason = autoStop
        };

        await Assert.That(AgentOrchestrator.StopReasonCode(agent)).IsEqualTo(expected);
    }
}
