using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Codex;

/// <summary>Event lines are verbatim <c>codex exec --json</c> output.</summary>
public class CodexExecEventsTests {
    static CodexExecEvents Fold(params string[] lines) {
        var events = new CodexExecEvents();
        foreach (var line in lines) events.Accept(line);

        return events;
    }

    [Test]
    public async Task A_completed_turn_reports_usage_with_cached_input_split_out() {
        var events = Fold(
            """{"type":"thread.started","thread_id":"01a17ea8-4c4b-73e0-8dcb-b341a89e4031"}""",
            """{"type":"turn.started"}""",
            """{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"I’ll run the command.\n"}}""",
            """{"type":"item.completed","item":{"id":"item_2","type":"agent_message","text":"Exit code: `6`"}}""",
            """{"type":"turn.completed","usage":{"input_tokens":37443,"cached_input_tokens":28416,"cache_write_input_tokens":0,"output_tokens":110,"reasoning_output_tokens":0}}""");

        await Assert.That(events.Usage).IsEqualTo(new ClaudeCliUsage(9027, 110, 28416, 0, null));
        await Assert.That(events.LastAgentMessage).IsEqualTo("Exit code: `6`");
        await Assert.That(events.CompletedTurns).IsEqualTo(1);
        await Assert.That(events.TurnFailed).IsFalse();
    }

    [Test]
    public async Task A_failed_turn_keeps_its_error_and_reports_no_usage() {
        var events = Fold(
            """{"type":"thread.started","thread_id":"01a17ea5-bff2-7163-ad7f-fd4a7df4aa10"}""",
            """{"type":"item.completed","item":{"id":"item_0","type":"error","message":"Model metadata for `no-such-model-xyz` not found."}}""",
            """{"type":"turn.started"}""",
            """{"type":"error","message":"{\"type\":\"error\",\"status\":400,\"error\":{\"code\":\"model_not_found\"}}"}""",
            """{"type":"turn.failed","error":{"message":"{\"type\":\"error\",\"status\":400,\"error\":{\"message\":\"The 'no-such-model-xyz' model is not supported.\",\"code\":\"model_not_found\"}}"}}""");

        await Assert.That(events.TurnFailed).IsTrue();
        await Assert.That(events.FailureMessage!).Contains("no-such-model-xyz");
        await Assert.That(events.Usage).IsNull();
        await Assert.That(CodexExecEvents.ClassifyFailure(events.FailureMessage)).IsEqualTo(ClaudeCliFailure.ProcessFailure);
    }

    [Test]
    public async Task Lines_that_are_not_events_are_ignored() {
        var events = Fold("", "not json", "2026-10-27T18:36:02Z ERROR codex_api: failed");

        await Assert.That(events.Usage).IsNull();
        await Assert.That(events.FailureMessage).IsNull();
    }

    [Test]
    public async Task A_rejected_credential_classifies_as_not_signed_in() {
        const string message =
            "unexpected status 401 Unauthorized: Missing bearer or basic authentication in header, url: https://api.openai.com/v1/responses";

        await Assert.That(CodexExecEvents.ClassifyFailure(message)).IsEqualTo(ClaudeCliFailure.NotSignedIn);
        await Assert.That(CodexExecEvents.ClassifyFailure("refresh_token_failed")).IsEqualTo(ClaudeCliFailure.NotSignedIn);
    }

    [Test]
    [Arguments("{\"error\":{\"code\":\"usage_limit_reached\"}}")]
    [Arguments("You've hit your usage limit. Try again later.")]
    [Arguments("{\"error\":{\"code\":\"insufficient_quota\"}}")]
    public async Task A_usage_limit_classifies_as_spend_budget(string message) {
        await Assert.That(CodexExecEvents.ClassifyFailure(message)).IsEqualTo(ClaudeCliFailure.SpendBudget);
    }
}
