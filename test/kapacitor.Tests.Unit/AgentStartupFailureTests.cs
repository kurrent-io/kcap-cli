using kapacitor.Daemon.Services;

namespace kapacitor.Tests.Unit;

/// <summary>
/// Tests <see cref="AgentOrchestrator.IsStartupFailure"/>, the discriminator
/// between a process that exited before establishing a real session (auth
/// error, missing config, immediate crash) and a real session that ended.
///
/// Regression for AI-572: a user who types <c>/exit</c> shortly after starting
/// the agent was being mislabeled as "failed during startup" because the prior
/// implementation used wall-clock time since spawn instead of output flow.
/// </summary>
public class AgentStartupFailureTests {
    static readonly DateTime SpawnedAt = new(2026, 5, 10, 11, 45, 11, DateTimeKind.Utc);

    [Test]
    public async Task NoOutputEverReceived_IsStartupFailure() {
        // LastOutputAt defaults to construction time, so it equals CreatedAt
        // when no output ever arrives. That gap of zero is below the threshold.
        var result = AgentOrchestrator.IsStartupFailure(SpawnedAt, SpawnedAt);

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task ErrorBannerThenImmediateExit_IsStartupFailure() {
        // Auth error / missing config: Claude prints one banner then quits.
        // Output flowed for ~200ms — well below the threshold.
        var result = AgentOrchestrator.IsStartupFailure(
            SpawnedAt,
            SpawnedAt.AddMilliseconds(200)
        );

        await Assert.That(result).IsTrue();
    }

    [Test]
    public async Task UserExitedAfterShortInteractiveSession_IsNotStartupFailure() {
        // AI-572 scenario: agent ran for ~27 seconds, user typed /exit. The
        // 30-second wall-clock window misclassified this as a startup failure.
        // Output flowed throughout, so the spawn → last-output gap is large.
        var result = AgentOrchestrator.IsStartupFailure(
            SpawnedAt,
            SpawnedAt.AddSeconds(27)
        );

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task LongSessionEndedNormally_IsNotStartupFailure() {
        var result = AgentOrchestrator.IsStartupFailure(
            SpawnedAt,
            SpawnedAt.AddMinutes(15)
        );

        await Assert.That(result).IsFalse();
    }

    [Test]
    public async Task MidSessionCrash_IsNotStartupFailure() {
        // A non-zero exit after sustained output is a runtime failure, not a
        // startup failure — the session ran fine, then something crashed.
        // The orchestrator still marks the agent "Failed" via exit code, but
        // it does not call LaunchFailedAsync.
        var result = AgentOrchestrator.IsStartupFailure(
            SpawnedAt,
            SpawnedAt.AddSeconds(60)
        );

        await Assert.That(result).IsFalse();
    }
}
