using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// AI-1357 Task 4: the spawn-before-post decision for the JSON-payload vendor dispatchers
/// (Kiro, OpenCode, Pi, Copilot). Capture must start on <c>Posted</c> OR <c>Spooled</c> — never
/// gated behind lifecycle-POST delivery. Only a permanent <c>Failed</c> withholds the watcher.
/// </summary>
public class SpawnBeforePostTests {
    [Test]
    public async Task spawn_after_spooled_and_posted_but_not_failed() {
        await Assert.That(AgentHookPoster.ShouldSpawnAfter(HookPostOutcome.Posted)).IsTrue();
        await Assert.That(AgentHookPoster.ShouldSpawnAfter(HookPostOutcome.Spooled)).IsTrue();
        await Assert.That(AgentHookPoster.ShouldSpawnAfter(HookPostOutcome.AuthLapsed)).IsTrue(); // legacy AuthLapsed also spawns now
        await Assert.That(AgentHookPoster.ShouldSpawnAfter(HookPostOutcome.Failed)).IsFalse();
    }
}
