using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Task 6: Antigravity is routed through <see cref="AgentHookPoster.PostOrSpoolAsync"/>
/// (its own bespoke poster previously gated the watcher on <c>exit == 0</c>, which never spawned
/// on a lapse/outage). <see cref="AntigravityHookCommand.SpawnGateForTest"/> exposes the same
/// spawn decision as <see cref="AgentHookPoster.ShouldSpawnAfter"/> so a spooled outcome still
/// spawns the watcher — capture must not depend on lifecycle delivery.
/// </summary>
public class AntigravitySpawnBeforePostTests {
    [Test]
    public async Task spooled_outcome_still_spawns_watcher() {
        await Assert.That(AntigravityHookCommand.SpawnGateForTest(HookPostOutcome.Spooled)).IsTrue();
        await Assert.That(AntigravityHookCommand.SpawnGateForTest(HookPostOutcome.Failed)).IsFalse();
    }
}
