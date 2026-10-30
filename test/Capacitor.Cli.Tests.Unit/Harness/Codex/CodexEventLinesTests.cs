using Capacitor.Cli.Harness.Codex;

namespace Capacitor.Cli.Tests.Unit.Harness.Codex;

/// <summary>The Codex rollout lines the server's normalizer stores an event for, and the per-turn
/// <c>event_msg</c> and <c>turn_context</c> records every turn ends with, which it never does.</summary>
public class CodexEventLinesTests {
    [Test]
    [Arguments("""{"type":"session_meta","payload":{"id":"x"}}""")]
    [Arguments("""{"type":"response_item","payload":{"type":"message","role":"assistant","content":[]}}""")]
    [Arguments("""{"type":"response_item","payload":{"type":"function_call","name":"shell","call_id":"c1"}}""")]
    [Arguments("""{"type":"compacted","payload":{"message":"summary"}}""")]
    public async Task A_line_the_server_can_store_counts(string line) {
        await Assert.That(CodexEventLines.CanAddEvents(line)).IsTrue();
    }

    [Test]
    [Arguments("""{"type":"event_msg","payload":{"type":"token_count"}}""")]
    [Arguments("""{"type":"event_msg","payload":{"type":"task_complete"}}""")]
    [Arguments("""{"type":"event_msg","payload":{"type":"turn_aborted"}}""")]
    [Arguments("""{"type":"turn_context","payload":{"model":"gpt-5"}}""")]
    [Arguments("""{"type":"world_state"}""")]
    [Arguments("not json")]
    public async Task A_line_the_server_stores_nothing_for_does_not(string line) {
        await Assert.That(CodexEventLines.CanAddEvents(line)).IsFalse();
    }
}
