using Capacitor.Cli.Core.Harness.Grok;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Grok;

public class GrokHookEnvironmentTests {
    [Test]
    public async Task A_set_hook_event_marks_a_grok_hook() {
        await Assert.That(GrokHookEnvironment.IsGrokHook(k => k == "GROK_HOOK_EVENT" ? "pre_tool_use" : null)).IsTrue();
    }

    /// <summary>An exported-but-empty variable is not Grok running the hook, and must not silence it.</summary>
    [Test]
    [Arguments(null)]
    [Arguments("")]
    public async Task An_unset_or_empty_hook_event_is_not_a_grok_hook(string? value) {
        await Assert.That(GrokHookEnvironment.IsGrokHook(_ => value)).IsFalse();
    }
}
