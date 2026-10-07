using Capacitor.Models.Transcripts.Harness.MistralVibe;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.MistralVibe;

public class MistralVibeTranscriptEventsTests {
    static readonly DateTimeOffset Received = new(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);

    static ProjectionResult P(string line) =>
        MistralVibeTranscriptEvents.Instance.Project(line, 1, Received,
            MistralVibeTranscriptEvents.Instance.CreateContext("sess", null));

    static IReadOnlyList<CanonicalEvent> E(string line) => P(line).Events;

    // ── legacy messages.jsonl ──────────────────────────────────────────────────────────────────

    [Test]
    public async Task Legacy_user_message_becomes_a_user_message_on_the_line_hash_id() {
        const string line = """{"role":"user","content":"hello"}""";
        var events = E(line);
        await Assert.That(events.Count).IsEqualTo(1);
        await Assert.That(((UserMessageReceived)events[0].Payload).Content).IsEqualTo("hello");
        await Assert.That(events[0].EventId).IsEqualTo(TranscriptIds.VibeRecord(line));
    }

    [Test]
    public async Task Legacy_assistant_text_and_tool_calls_emit_two_events() {
        const string line = """{"role":"assistant","content":"on it","tool_calls":[{"id":"call_1","type":"function","function":{"name":"file_system.bash","arguments":"{\"cmd\":\"ls\"}"}}]}""";
        var events = E(line);
        await Assert.That(events.Count).IsEqualTo(2);

        await Assert.That(((AssistantTextGenerated)events[0].Payload).Content).IsEqualTo("on it");
        await Assert.That(events[0].EventId).IsEqualTo(TranscriptIds.VibeRecord(line));

        var calls = (AssistantToolCallsGenerated)events[1].Payload;
        await Assert.That(calls.ToolCalls[0].ToolName).IsEqualTo("bash"); // group prefix stripped
        await Assert.That(calls.ToolCalls[0].CallId).IsEqualTo("call_1");
        await Assert.That(calls.ToolCalls[0].ToolKind).IsEqualTo(AcpToolKind.Execute);
        await Assert.That(events[1].EventId).IsEqualTo(TranscriptIds.Sibling(TranscriptIds.VibeRecord(line), "tool_calls"));
    }

    [Test]
    public async Task Legacy_tool_role_becomes_a_tool_result_paired_by_call_id() {
        const string line = """{"role":"tool","tool_call_id":"call_1","content":"file.txt"}""";
        var result = (ToolResultReceived)E(line)[0].Payload;
        await Assert.That(result.CallId).IsEqualTo("call_1");
        await Assert.That(result.Result).IsEqualTo("file.txt");
    }

    [Test]
    public async Task Legacy_system_message_is_not_recorded() {
        await Assert.That(E("""{"role":"system","content":"you are helpful"}""").Count).IsEqualTo(0);
    }

    // ── unified store entries ──────────────────────────────────────────────────────────────────

    [Test]
    public async Task Unified_message_entry_reads_role_content_array_and_uses_the_stable_id() {
        // Real vibe 2.26.0 unified entry: content is a text-block array, createdAt is epoch ms, id is stable.
        const string entry = """{"type":"message","role":"user","content":[{"type":"text","text":"hi there"}],"createdAt":1791384615123,"id":"user-turn-abc-1"}""";
        var events = E(entry);
        await Assert.That(((UserMessageReceived)events[0].Payload).Content).IsEqualTo("hi there");
        await Assert.That(events[0].EventId).IsEqualTo(TranscriptIds.VibeEntry("user-turn-abc-1"));
        // Numeric createdAt is resolved to the real time, not the batch receive time.
        await Assert.That(events[0].Timestamp).IsEqualTo(DateTimeOffset.FromUnixTimeMilliseconds(1791384615123));
    }

    [Test]
    public async Task Unified_reasoning_becomes_thinking() {
        var thinking = (AssistantThinkingGenerated)E("""{"type":"reasoning","role":"assistant","content":[{"type":"text","text":"let me think"}],"createdAt":1000,"id":"r1"}""")[0].Payload;
        await Assert.That(thinking.Content).IsEqualTo("let me think");
    }

    [Test]
    public async Task Unified_effect_emits_a_tool_call_and_a_paired_result() {
        const string line = """{"type":"effect","createdAt":"2026-10-07T00:00:00Z","call":{"id":"c9","tool":"file_system.read","arguments":{"path":"a.txt"}},"result":{"output":"contents"}}""";
        var events = E(line);
        await Assert.That(events.Count).IsEqualTo(2);

        var calls = (AssistantToolCallsGenerated)events[0].Payload;
        await Assert.That(calls.ToolCalls[0].ToolName).IsEqualTo("read");
        await Assert.That(calls.ToolCalls[0].ToolKind).IsEqualTo(AcpToolKind.Read);
        await Assert.That(events[0].EventId).IsEqualTo(TranscriptIds.VibeRecord(line));

        var result = (ToolResultReceived)events[1].Payload;
        await Assert.That(result.CallId).IsEqualTo("c9");
        await Assert.That(result.Result).IsEqualTo("contents");
        await Assert.That(events[1].EventId).IsEqualTo(TranscriptIds.Sibling(TranscriptIds.VibeRecord(line), "result"));
    }

    [Test]
    [Arguments("""{"type":"notice","text":"x"}""")]
    [Arguments("""{"type":"checkpoint","at":"y"}""")]
    public async Task Unified_notice_and_checkpoint_are_ignored(string line) {
        await Assert.That(P(line).Events.Count).IsEqualTo(0);
        await Assert.That(P(line).Rejected).IsNull();
    }

    // ── framing ────────────────────────────────────────────────────────────────────────────────

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("not json")]
    [Arguments("[1,2,3]")]
    public async Task Non_object_lines_reject(string line) {
        await Assert.That(P(line).Rejected).IsNotNull();
    }
}
