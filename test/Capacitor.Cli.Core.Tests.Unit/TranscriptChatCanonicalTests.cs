using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Models.Transcripts.Harness.Claude;
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Cli.Core.Tests.Unit;

public class TranscriptChatCanonicalTests {
    static CanonicalEvent Event(string type, object payload) => new(type, payload, Guid.NewGuid(), DateTimeOffset.UtcNow);

    [Test]
    public async Task A_user_message_is_one_row_and_one_submitted_input_without_rules() {
        var result = TranscriptChat.Project(Event(CanonicalEventTypes.UserMessageReceived, new UserMessageReceived { Content = "hello" }), rules: null);
        await Assert.That(result.Envelopes.Count).IsEqualTo(1);
        await Assert.That(result.Envelopes[0].Kind).IsEqualTo(AcpEventKind.UserMessage);
        await Assert.That(result.SubmittedInputs).IsEquivalentTo(new[] { "hello" });
    }

    [Test]
    public async Task Tool_calls_become_one_row_each_and_a_result_settles_by_call_id() {
        var calls = new AssistantToolCallsGenerated();
        calls.ToolCalls.Add(new ToolCallInfo { CallId = "t1", ToolName = "Bash", Arguments = Struct.Parser.ParseJson("""{"command":"ls"}""") });
        var rows = TranscriptChat.Project(Event(CanonicalEventTypes.AssistantToolCallsGenerated, calls), rules: null).Envelopes;
        await Assert.That(rows.Count).IsEqualTo(1);
        await Assert.That(rows[0].ToolCallId).IsEqualTo("t1");
        await Assert.That(rows[0].ToolInputJson).IsEqualTo("""{"command":"ls"}""");

        var result = TranscriptChat.Project(Event(CanonicalEventTypes.ToolResultReceived, new ToolResultReceived { CallId = "t1", Result = "ok" }), rules: null).Envelopes;
        await Assert.That(result[0].Kind).IsEqualTo(AcpEventKind.ToolResult);
        await Assert.That(result[0].ToolCallId).IsEqualTo("t1");
    }

    [Test]
    public async Task Claude_rules_hide_a_meta_message_and_acknowledge_no_input_for_it() {
        var meta = new UserMessageReceived { Content = "<command-name>/clear</command-name>" };
        meta.Extensions[ClaudeCodeExtension.Slug] = Struct.Parser.ParseJson($$"""{"{{ClaudeCodeExtension.IsMeta}}":true}""");
        var result = TranscriptChat.Project(Event(CanonicalEventTypes.UserMessageReceived, meta), ClaudeChatRules.Instance);
        await Assert.That(result.Envelopes).IsEmpty();
        await Assert.That(result.SubmittedInputs).IsEmpty();
    }

    [Test]
    public async Task RulesFor_names_the_two_vendors_with_chat_rules() {
        await Assert.That(TranscriptChat.RulesFor("Claude")).IsSameReferenceAs(ClaudeChatRules.Instance);
        await Assert.That(TranscriptChat.RulesFor("gemini")).IsNull();
    }
}
