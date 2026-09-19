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

    /// The server writes it from the subagent-stop hook, which says the subagent ended but not how.
    [Test]
    public async Task A_subagent_completion_finishes_by_agent_id_with_no_outcome_under_any_rules() {
        var at = new DateTimeOffset(2026, 9, 17, 10, 1, 0, TimeSpan.Zero);
        foreach (var rules in new IChatDisplayRules?[] { null, ClaudeChatRules.Instance }) {
            var result = TranscriptChat.Project(
                new CanonicalEvent(CanonicalEventTypes.SubagentCompleted, new SubagentCompleted { AgentId = "a1" }, Guid.NewGuid(), at), rules);
            await Assert.That(result.Envelopes).IsEmpty();
            await Assert.That(result.SubmittedInputs).IsEmpty();
            var finished = (SubagentSignal.Finished)result.Subagents.Single();
            await Assert.That(finished.CallId).IsNull();
            await Assert.That(finished.AgentId).IsEqualTo("a1");
            await Assert.That(finished.Outcome).IsNull();
            await Assert.That(finished.At).IsEqualTo(at);
        }
        await Assert.That(TranscriptChat.Project(Event(CanonicalEventTypes.SubagentCompleted, new SubagentCompleted()), ClaudeChatRules.Instance).Subagents).IsEmpty();
    }

    [Test]
    public async Task RulesFor_names_the_two_vendors_with_chat_rules() {
        await Assert.That(TranscriptChat.RulesFor("Claude")).IsSameReferenceAs(ClaudeChatRules.Instance);
        await Assert.That(TranscriptChat.RulesFor("gemini")).IsNull();
    }

    sealed class SignallingRules : IChatDisplayRules {
        public AcpEventEnvelope? Filter(CanonicalEvent evt, AcpEventEnvelope envelope) =>
            envelope.Kind == AcpEventKind.ToolCall ? null : envelope;

        public IReadOnlyList<SubagentSignal> Subagents(CanonicalEvent evt, AcpEventEnvelope raw) =>
            raw.Kind == AcpEventKind.ToolCall ? [new SubagentSignal.Started(raw.ToolCallId!, "explore", "look", evt.Timestamp)] : [];
    }

    sealed class SilentRules : IChatDisplayRules {
        public AcpEventEnvelope? Filter(CanonicalEvent evt, AcpEventEnvelope envelope) => envelope;
    }

    static AssistantToolCallsGenerated Calls(params string[] ids) {
        var calls = new AssistantToolCallsGenerated();
        foreach (var id in ids) calls.ToolCalls.Add(new ToolCallInfo { CallId = id, ToolName = "Agent", Arguments = new Struct() });
        return calls;
    }

    [Test]
    public async Task Signals_ride_beside_the_rows_and_a_hidden_row_still_yields_its_signal() {
        var evt = Event(CanonicalEventTypes.AssistantToolCallsGenerated, Calls("t1"));
        var result = TranscriptChat.Project(evt, new SignallingRules());
        await Assert.That(result.Envelopes).IsEmpty();
        await Assert.That(result.Subagents).Count().IsEqualTo(1);
        var started = (SubagentSignal.Started)result.Subagents[0];
        await Assert.That(started.CallId).IsEqualTo("t1");
        await Assert.That(started.Name).IsEqualTo("explore");
        await Assert.That(started.At).IsEqualTo(evt.Timestamp);
    }

    [Test]
    public async Task Rules_without_an_override_and_no_rules_yield_no_signals() {
        var evt = Event(CanonicalEventTypes.AssistantToolCallsGenerated, Calls("t1"));
        await Assert.That(TranscriptChat.Project(evt, new SilentRules()).Subagents).IsEmpty();
        await Assert.That(TranscriptChat.Project(evt, rules: null).Subagents).IsEmpty();
        await Assert.That(TranscriptChat.Project(evt, rules: null).Envelopes).Count().IsEqualTo(1);
    }

    [Test]
    public async Task ProjectWithInputs_collects_the_signals_of_every_event_on_a_line() {
        var chat = new TranscriptChatProjection(ClaudeTranscriptEvents.Instance, new SignallingRules());
        var line = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Agent","input":{}},{"type":"text","text":"hi"},{"type":"tool_use","id":"t2","name":"Agent","input":{}}]}}""";
        var result = chat.ProjectWithInputs(line, 1, DateTimeOffset.UnixEpoch, chat.CreateContext("s", null));
        await Assert.That(result.Envelopes.Select(e => e.Kind)).IsEquivalentTo(new[] { AcpEventKind.AssistantText });
        await Assert.That(result.Subagents.Cast<SubagentSignal.Started>().Select(s => s.CallId)).IsEquivalentTo(new[] { "t1", "t2" });
    }

    [Test]
    public async Task The_default_ProjectWithInputs_and_the_journal_projection_yield_no_signals() {
        var journal = TranscriptChat.Journal;
        var line = EnvelopeJournalFormat.Write(new AcpEventEnvelope(Kind: AcpEventKind.ToolCall, ToolCallId: "c1", ToolName: "Agent", ToolInputJson: "{}"));
        var result = journal.ProjectWithInputs(line, 1, DateTimeOffset.UnixEpoch, journal.CreateContext("s", null));
        await Assert.That(result.Envelopes).Count().IsEqualTo(1);
        await Assert.That(result.SubmittedInputs).IsEmpty();
        await Assert.That(result.Subagents).IsEmpty();
    }
}
