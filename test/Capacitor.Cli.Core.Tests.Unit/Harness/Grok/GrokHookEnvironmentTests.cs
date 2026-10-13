using Capacitor.Cli.Core.Harness.Grok;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Grok;

public class GrokHookEnvironmentTests {
    static Func<string, string?> Env(string? hookEvent) => k => k == "GROK_HOOK_EVENT" ? hookEvent : null;

    [Test]
    public async Task A_set_hook_event_marks_a_grok_hook() {
        await Assert.That(GrokHookEnvironment.IsGrokHook(Env("pre_tool_use"))).IsTrue();
    }

    /// <summary>An exported-but-empty variable is not Grok running the hook, and must not silence it.</summary>
    [Test]
    [Arguments(null)]
    [Arguments("")]
    public async Task An_unset_or_empty_hook_event_is_not_a_grok_hook(string? value) {
        await Assert.That(GrokHookEnvironment.IsGrokHook(Env(value))).IsFalse();
    }

    [Test]
    public async Task A_payload_naming_the_event_grok_announced_is_grok_s() {
        const string body = """{"hookEventName":"pre_tool_use","hook_event_name":"PreToolUse","sessionId":"s"}""";

        await Assert.That(GrokHookEnvironment.IsGrokPayload(body, Env("pre_tool_use"))).IsTrue();
    }

    /// <summary>A session started beneath a Grok hook inherits the variable; its own hooks carry
    /// their own vendor's payload and must still run.</summary>
    [Test]
    [Arguments("""{"hook_event_name":"PreToolUse","session_id":"s"}""")]
    [Arguments("""{"hookEventName":"stop","sessionId":"s"}""")]
    [Arguments("not json")]
    [Arguments("")]
    public async Task A_payload_that_does_not_name_grok_s_event_is_not_grok_s(string body) {
        await Assert.That(GrokHookEnvironment.IsGrokPayload(body, Env("pre_tool_use"))).IsFalse();
    }

    [Test]
    public async Task No_payload_is_grok_s_without_the_variable() {
        const string body = """{"hookEventName":"pre_tool_use"}""";

        await Assert.That(GrokHookEnvironment.IsGrokPayload(body, Env(null))).IsFalse();
    }
}
