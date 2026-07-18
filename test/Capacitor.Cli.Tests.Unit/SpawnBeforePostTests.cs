using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Task 4: the spawn-before-post decision for the JSON-payload vendor dispatchers
/// (Kiro, OpenCode, Pi, Copilot). Capture must start on <c>Posted</c> OR <c>Spooled</c> — never
/// gated behind lifecycle-POST delivery. Only a permanent <c>Failed</c> withholds the watcher.
/// </summary>
public class SpawnBeforePostTests {
    [Test]
    public async Task spawn_after_posted_or_spooled_only() {
        await Assert.That(AgentHookPoster.ShouldSpawnAfter(HookPostOutcome.Posted)).IsTrue();
        await Assert.That(AgentHookPoster.ShouldSpawnAfter(HookPostOutcome.Spooled)).IsTrue();
        // AuthLapsed (legacy PostAsync path) spools NOTHING, so spawning there would tail a session
        // whose SessionStarted was permanently dropped — must NOT spawn.
        await Assert.That(AgentHookPoster.ShouldSpawnAfter(HookPostOutcome.AuthLapsed)).IsFalse();
        await Assert.That(AgentHookPoster.ShouldSpawnAfter(HookPostOutcome.Failed)).IsFalse();
    }
}
