using Capacitor.Cli.Harness.Claude;

namespace Capacitor.Cli.Tests.Unit.Harness.Claude;

/// <summary>The Claude lines the server's normalizer stores an event for, and the ones it never does —
/// among them every record Claude Code writes after a turn's last message.</summary>
public class ClaudeEventLinesTests {
    [Test]
    [Arguments("""{"type":"user","message":{"role":"user","content":"fix the build"}}""")]
    [Arguments("""{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]}}""")]
    [Arguments("""{"type":"assistant","message":{"content":[{"type":"text","text":"done"}]}}""")]
    [Arguments("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{}}]}}""")]
    [Arguments("""{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"hmm"}]}}""")]
    [Arguments("""{"type":"attachment","attachment":{"type":"queued_command","prompt":"next"}}""")]
    [Arguments("""{"type":"system","subtype":"local_command","commandRun":{"command":"model","args":""}}""")]
    public async Task A_line_the_server_can_store_counts(string line) {
        await Assert.That(ClaudeEventLines.CanAddEvents(line)).IsTrue();
    }

    [Test]
    [Arguments("""{"type":"system","subtype":"stop_hook_summary"}""")]
    [Arguments("""{"type":"system","subtype":"turn_duration","durationMs":1200}""")]
    [Arguments("""{"type":"system","subtype":"away_summary"}""")]
    [Arguments("""{"type":"last-prompt","lastPrompt":"fix the build"}""")]
    [Arguments("""{"type":"file-history-snapshot","snapshot":{}}""")]
    [Arguments("""{"type":"ai-title","aiTitle":"Fix the build"}""")]
    [Arguments("""{"type":"custom-title","customTitle":"Build"}""")]
    [Arguments("""{"type":"permission-mode","mode":"default"}""")]
    [Arguments("""{"type":"cost-state"}""")]
    [Arguments("""{"type":"attachment","attachment":{"type":"hook_success"}}""")]
    [Arguments("""{"type":"assistant","message":{"content":[]}}""")]
    [Arguments("""{"type":"user","message":{"content":[]}}""")]
    [Arguments("not json")]
    public async Task A_line_the_server_stores_nothing_for_does_not(string line) {
        await Assert.That(ClaudeEventLines.CanAddEvents(line)).IsFalse();
    }
}
