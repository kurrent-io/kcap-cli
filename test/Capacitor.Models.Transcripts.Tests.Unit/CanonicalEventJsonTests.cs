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
    public async Task UnknownTypesAndMalformedPayloadsReadAsNull() {
        await Assert.That(CanonicalEventJson.TryParse("InterruptIssued", """{"request_id":"r1"}""")).IsNull();
        await Assert.That(CanonicalEventJson.TryParse(CanonicalEventTypes.SessionStarted, """{}""")).IsNull();
        await Assert.That(CanonicalEventJson.TryParse(CanonicalEventTypes.UserMessageReceived, "not json")).IsNull();
        await Assert.That(CanonicalEventJson.TryParse(CanonicalEventTypes.UserMessageReceived, """{"content":42}""")).IsNull();
    }
}
