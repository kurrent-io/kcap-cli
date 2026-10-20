using Kurrent.Agent.Schema.Events;

namespace Capacitor.Models.Transcripts.Tests.Unit;

public class CanonicalEventJsonTests {
    [Test]
    public async Task ReadsEachConversationalTypeBySnakeCaseName() {
        var user = CanonicalEventJson.TryParse(CanonicalEventTypes.UserMessageReceived, """{"content":"hello","brand_new":1}""");
        await Assert.That(user).IsTypeOf<UserMessageReceived>();
        await Assert.That(((UserMessageReceived)user!).Content).IsEqualTo("hello");

        var calls = CanonicalEventJson.TryParse(CanonicalEventTypes.AssistantToolCallsGenerated,
            """{"tool_calls":[{"call_id":"t1","tool_name":"Bash","arguments":{"command":"ls"}}]}""");
        var call = ((AssistantToolCallsGenerated)calls!).ToolCalls[0];
        await Assert.That(call.CallId).IsEqualTo("t1");
        await Assert.That(call.ToolName).IsEqualTo("Bash");
        await Assert.That(call.Arguments!.Fields["command"].StringValue).IsEqualTo("ls");

        var result = CanonicalEventJson.TryParse(CanonicalEventTypes.ToolResultReceived, """{"call_id":"t1","result":"ok"}""");
        await Assert.That(((ToolResultReceived)result!).Result).IsEqualTo("ok");
        await Assert.That(CanonicalEventJson.TryParse(CanonicalEventTypes.AssistantTextGenerated, """{"content":"Hi"}""")).IsTypeOf<AssistantTextGenerated>();
        await Assert.That(CanonicalEventJson.TryParse(CanonicalEventTypes.AssistantThinkingGenerated, """{"content":"hm"}""")).IsTypeOf<AssistantThinkingGenerated>();
    }

    [Test]
    public async Task ReadsTheServersSubagentCompletion() {
        var completed = CanonicalEventJson.TryParse(CanonicalEventTypes.SubagentCompleted,
            """{"agent_id":"a9f262478e032f427","timestamp":"2026-09-17T10:01:00Z"}""");
        await Assert.That(completed).IsTypeOf<SubagentCompleted>();
        await Assert.That(((SubagentCompleted)completed!).AgentId).IsEqualTo("a9f262478e032f427");
    }

    [Test]
    public async Task ReadsTheServersSubagentStart() {
        var started = CanonicalEventJson.TryParse(CanonicalEventTypes.SubagentStarted, """{"agent_id":"a9f262478e032f427","agent_type":"Explore"}""");
        await Assert.That(started).IsTypeOf<SubagentStarted>();
        await Assert.That(((SubagentStarted)started!).AgentId).IsEqualTo("a9f262478e032f427");
    }

    [Test]
    public async Task UnknownTypesAndMalformedPayloadsReadAsNull() {
        await Assert.That(CanonicalEventJson.TryParse("InterruptIssued", """{"request_id":"r1"}""")).IsNull();
        await Assert.That(CanonicalEventJson.TryParse(CanonicalEventTypes.SessionStarted, """{}""")).IsNull();
        await Assert.That(CanonicalEventJson.TryParse(CanonicalEventTypes.UserMessageReceived, "not json")).IsNull();
        await Assert.That(CanonicalEventJson.TryParse(CanonicalEventTypes.UserMessageReceived, """{"content":42}""")).IsNull();
    }

    /// <summary>A stored compaction reaches a remote chat as the note a local one shows.</summary>
    [Test]
    public async Task A_stored_compaction_reads_back_as_its_record() {
        var parsed = CanonicalEventJson.TryParse(CanonicalEventTypes.ContextCompacted, """{"encrypted_content":"x","replacement_history":[]}""");

        await Assert.That(parsed).IsTypeOf<ContextCompactedEvent>();
        await Assert.That(((ContextCompactedEvent)parsed!).EncryptedContent).IsEqualTo("x");
    }
}
