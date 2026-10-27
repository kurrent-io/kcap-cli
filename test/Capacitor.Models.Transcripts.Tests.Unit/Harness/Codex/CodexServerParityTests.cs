using Capacitor.Models.Transcripts.Harness.Codex;
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Codex;

/// <summary>The rules the server's Codex normalizer applies, which the leaf must reproduce before the
/// server can call it: the session and handoff records, telemetry and timing as amendments, the usage
/// cluster and its finalization, web-search pairing across batches, and compaction.</summary>
public class CodexServerParityTests {
    static readonly DateTimeOffset Received = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    static readonly CodexRolloutEvents Projection = CodexRolloutEvents.Instance;

    static TranscriptContext Ctx() => Projection.CreateContext("sess", null);

    static ProjectionResult P(string line, TranscriptContext ctx) => Projection.Project(line, 1, Received, ctx);

    static ProjectionResult P(string line) => P(line, Ctx());

    static string Env(string type, string payload, string ts = "2026-09-04T09:00:00Z") =>
        $$$"""{"timestamp":"{{{ts}}}","type":"{{{type}}}","payload":{{{payload}}}}""";

    static string Item(string payload, string ts = "2026-09-04T09:00:00Z") => Env("response_item", payload, ts);

    static string Msg(string payload, string ts = "2026-09-04T09:00:00Z") => Env("event_msg", payload, ts);

    static string Assistant(string text) => Item($$"""{"type":"message","role":"assistant","content":[{"type":"output_text","text":"{{text}}"}]}""");

    const string Thinking = """{"type":"reasoning","summary":[],"encrypted_content":"blob"}""";

    static string Call(string id, string name = "exec_command") =>
        Item($$"""{"type":"function_call","name":"{{name}}","call_id":"{{id}}","arguments":"{\"cmd\":\"ls\"}"}""");

    static string Output(string id) => Item($$"""{"type":"function_call_output","call_id":"{{id}}","output":"ok"}""");

    static string Tokens(long input, long cached = 0, long output = 0, string extra = "") =>
        Msg($$$"""{"type":"token_count","info":{"last_token_usage":{"input_tokens":{{{input}}},"cached_input_tokens":{{{cached}}},"output_tokens":{{{output}}},"reasoning_output_tokens":3}{{{extra}}}}}""");

    static Struct Codex(object payload) => SchemaExtensions.Slug(payload, CodexExtension.Slug)!;

    static UsageApplied Applied(ProjectionResult r) => (UsageApplied)r.Events.Single().Payload;

    static string Ids(UsageApplied applied) => string.Join(",", applied.Targets.Select(t => t.EventId));

    static string Echoes(UsageApplied applied) => string.Join(",", applied.Targets.Select(t => t.IsEcho));

    [Test]
    public async Task Session_meta_starts_the_session_with_every_codex_field_written() {
        var e = P(Env("session_meta", """{"cwd":"/w","originator":"codex_cli_rs","cli_version":"0.150.0","source":"cli","model_provider":"openai","git":{"commit_hash":"c","branch":"main"}}""")).Events;

        await Assert.That(e).Count().IsEqualTo(1);
        await Assert.That(e[0].EventType).IsEqualTo(CanonicalEventTypes.SessionStarted);
        var codex = Codex(e[0].Payload);
        await Assert.That(codex.Fields["cwd"].StringValue).IsEqualTo("/w");
        await Assert.That(codex.Fields["cli_version"].StringValue).IsEqualTo("0.150.0");
        await Assert.That(codex.Fields["model_provider"].StringValue).IsEqualTo("openai");
        await Assert.That(codex.Fields["git"].StructValue.Fields["branch"].StringValue).IsEqualTo("main");
        await Assert.That(codex.Fields["git"].StructValue.Fields["repository_url"].KindCase).IsEqualTo(Value.KindOneofCase.NullValue);
        await Assert.That(codex.Fields["workspace_root"].KindCase).IsEqualTo(Value.KindOneofCase.NullValue);
    }

    [Test]
    [Arguments("""{"thread_source":"subagent","cwd":"/w"}""")]
    [Arguments("""{"cwd":"/w","source":{"subagent":{"depth":1}}}""")]
    public async Task A_subagent_rollout_starts_no_session(string payload) =>
        await Assert.That(P(Env("session_meta", payload)).Events).IsEmpty();

    [Test]
    public async Task Assistant_text_keeps_its_phase_and_an_agent_message_its_routing() {
        var phased = P(Item("""{"type":"message","role":"assistant","phase":"final","content":[{"type":"output_text","text":"done"}]}""")).Events;
        await Assert.That(Codex(phased[0].Payload).Fields["phase"].StringValue).IsEqualTo("final");

        var handoff = P(Item("""{"type":"agent_message","author":"/root/a","recipient":"/root","content":[{"type":"input_text","text":"report"}]}""")).Events;
        await Assert.That(((AssistantTextGenerated)handoff[0].Payload).Content).IsEqualTo("report");
        var routing = Codex(handoff[0].Payload).Fields[CodexExtension.AgentMessage].StructValue;
        await Assert.That(routing.Fields["author"].StringValue).IsEqualTo("/root/a");
        await Assert.That(routing.Fields["recipient"].StringValue).IsEqualTo("/root");

        await Assert.That(P(Item("""{"type":"agent_message","author":"/root/a","content":[]}""")).Events).IsEmpty();
    }

    [Test]
    public async Task Reasoning_keeps_its_encrypted_blob_and_prefers_plain_content() {
        var e = P(Item("""{"type":"reasoning","content":"plain","summary":[{"type":"summary_text","text":"s"}],"encrypted_content":"blob"}""")).Events;
        var thinking = (AssistantThinkingGenerated)e[0].Payload;
        await Assert.That(thinking.Content).IsEqualTo("plain");
        var openai = SchemaExtensions.Slug(thinking, OpenAiExtension.Slug)!;
        await Assert.That(openai.Fields["thinking"].StructValue.Fields["raw"].StringValue).IsEqualTo("blob");
    }

    [Test]
    public async Task A_namespaced_tool_joins_its_name_and_keeps_the_split() {
        var e = P(Item("""{"type":"function_call","name":"get_issue","namespace":"mcp__linear__","call_id":"c","arguments":"{\"id\":\"1\"}"}""")).Events;
        var call = ((AssistantToolCallsGenerated)e[0].Payload).ToolCalls[0];
        await Assert.That(call.ToolName).IsEqualTo("mcp__linear__get_issue");
        await Assert.That(Codex(e[0].Payload).Fields["name"].StringValue).IsEqualTo("get_issue");
        await Assert.That(Codex(e[0].Payload).Fields["namespace"].StringValue).IsEqualTo("mcp__linear__");
    }

    [Test]
    [Arguments("""{"type":"function_call","call_id":"c","arguments":"{}"}""")]
    [Arguments("""{"type":"function_call","name":"f","arguments":"{}"}""")]
    [Arguments("""{"type":"function_call_output","output":"x"}""")]
    public async Task A_call_or_output_without_its_ids_emits_nothing(string payload) =>
        await Assert.That(P(Item(payload)).Events).IsEmpty();

    /// The two orders Codex writes telemetry in: before its output it rides the output as returned,
    /// after it an amendment names the output, which is never touched once returned.
    [Test]
    public async Task Exec_telemetry_merges_before_its_output_and_amends_after_it() {
        const string exec = """{"type":"exec_command_end","call_id":"c1","exit_code":2,"duration":{"secs":1,"nanos":500000000},"stdout":"","stderr":"err","parsed_cmd":[{"type":"unknown"}]}""";

        var ctx = Ctx();
        await Assert.That(P(Msg(exec), ctx).Events).IsEmpty();
        var merged = Codex(P(Output("c1"), ctx).Events[0].Payload).Fields[CodexExtension.Exec].StructValue;
        await Assert.That(merged.Fields["exit_code"].NumberValue).IsEqualTo(2d);
        await Assert.That(merged.Fields["duration_ms"].NumberValue).IsEqualTo(1500d);
        await Assert.That(merged.Fields["stdout"].KindCase).IsEqualTo(Value.KindOneofCase.NullValue);

        ctx = Ctx();
        var output  = P(Output("c1"), ctx).Events[0];
        var amended = P(Msg(exec), ctx);
        await Assert.That(amended.Events).IsEmpty();
        var amendment = amended.Amendments.Single();
        await Assert.That(amendment.TargetEventId).IsEqualTo(output.EventId);
        await Assert.That(amendment.Slug).IsEqualTo(CodexExtension.Slug);
        await Assert.That(amendment.Extension.Fields[CodexExtension.Exec].StructValue.Fields["stderr"].StringValue).IsEqualTo("err");
        await Assert.That(SchemaExtensions.Slug(output.Payload, CodexExtension.Slug)).IsNull();

        // The first telemetry to amend a result takes it out of the index; a second one waits.
        await Assert.That(P(Msg(exec), ctx).Amendments).IsEmpty();
    }

    [Test]
    public async Task Patch_telemetry_records_every_change_and_its_outcome() {
        var ctx    = Ctx();
        var output = P(Item("""{"type":"custom_tool_call_output","call_id":"p1","output":"Success."}"""), ctx).Events[0];
        var patch  = P(Msg("""{"type":"patch_apply_end","call_id":"p1","success":false,"changes":{"/w/a.rs":{"type":"add","content":"x"}}}"""), ctx).Amendments.Single();

        await Assert.That(patch.TargetEventId).IsEqualTo(output.EventId);
        var block = patch.Extension.Fields[CodexExtension.Patch].StructValue;
        await Assert.That(block.Fields["success"].BoolValue).IsFalse();
        var change = block.Fields["changes"].ListValue.Values[0].StructValue;
        await Assert.That(change.Fields["file_path"].StringValue).IsEqualTo("/w/a.rs");
        await Assert.That(change.Fields["kind"].StringValue).IsEqualTo("add");
    }

    /// The stashes and the emitted-result index mean nothing once the batch they were built in is
    /// written; telemetry across a boundary is lost, as on the server.
    [Test]
    public async Task A_batch_boundary_forgets_telemetry_waiting_on_either_side() {
        var ctx = Ctx();
        P(Output("c1"), ctx);
        ctx.BeginBatch();
        await Assert.That(P(Msg("""{"type":"exec_command_end","call_id":"c1","exit_code":0}"""), ctx).Amendments).IsEmpty();

        ctx.BeginBatch();
        var output = P(Output("c1"), ctx).Events[0];
        await Assert.That(SchemaExtensions.Slug(output.Payload, CodexExtension.Slug)).IsNull();
    }

    [Test]
    public async Task Task_timing_amends_the_first_event_of_the_cluster_in_any_batch() {
        var ctx = Ctx();
        await Assert.That(P(Msg("""{"type":"task_complete","duration_ms":10}"""), ctx).Amendments).IsEmpty();

        var first = P(Item(Thinking), ctx).Events[0];
        P(Assistant("a"), ctx);
        P(Tokens(10), ctx);
        ctx.BeginBatch();

        var amendment = P(Msg("""{"type":"task_complete","duration_ms":4200,"time_to_first_token_ms":350}"""), ctx).Amendments.Single();
        await Assert.That(amendment.TargetEventId).IsEqualTo(first.EventId);
        var task = amendment.Extension.Fields[CodexExtension.Task].StructValue;
        await Assert.That(task.Fields["duration_ms"].NumberValue).IsEqualTo(4200d);
        await Assert.That(task.Fields["time_to_first_token_ms"].NumberValue).IsEqualTo(350d);
    }

    /// One usage per cluster: the first non-thinking member is canonical, the rest echo it, input is
    /// net of cached input, and the model is the last turn's.
    [Test]
    public async Task Token_count_applies_one_usage_to_its_cluster_with_one_canonical_member() {
        var ctx = Ctx();
        P(Env("turn_context", """{"model":"gpt-5-codex"}"""), ctx);
        var thinking = P(Item(Thinking), ctx).Events[0];
        var text     = P(Assistant("a"), ctx).Events[0];
        var call     = P(Call("c1"), ctx).Events[0];
        P(Output("c1"), ctx);

        var result  = P(Tokens(16177, cached: 6528, output: 288, extra: ""","model_context_window":258400"""), ctx);
        var applied = Applied(result);

        await Assert.That(result.Events[0].EventType).IsEqualTo(CanonicalEventTypes.UsageApplied);
        await Assert.That(result.Events[0].EventId).IsEqualTo(TranscriptIds.Sibling(thinking.EventId, "usage-backfill"));
        await Assert.That(applied.AnchorEventId).IsEqualTo(thinking.EventId);
        await Assert.That(applied.Usage.InputTokens).IsEqualTo(9649L);
        await Assert.That(applied.Usage.CachedInputTokens).IsEqualTo(6528L);
        await Assert.That(applied.Usage.OutputTokens).IsEqualTo(288L);
        await Assert.That(applied.Usage.ReasoningTokens).IsEqualTo(3L);
        await Assert.That(applied.Usage.TotalTokens).IsEqualTo(16465L);
        await Assert.That(applied.Usage.Model).IsEqualTo("gpt-5-codex");
        await Assert.That(applied.Usage.AdditionalCounts.Fields["model_context_window"].NumberValue).IsEqualTo(258400d);

        await Assert.That(Ids(applied)).IsEqualTo(string.Join(",", thinking.EventId, text.EventId, call.EventId));
        await Assert.That(Echoes(applied)).IsEqualTo("True,False,True");
        await Assert.That(applied.Targets[2].ToolName).IsEqualTo("exec_command");
    }

    /// A line read twice is one event: it is one usage target, and once its cluster is counted it joins no later one.
    [Test]
    public async Task A_replayed_line_is_one_cluster_member() {
        var ctx  = Ctx();
        var text = P(Assistant("a"), ctx).Events[0];
        P(Assistant("a"), ctx);
        await Assert.That(Ids(Applied(P(Tokens(10), ctx)))).IsEqualTo(text.EventId.ToString());

        P(Assistant("a"), ctx);
        var next = P(Assistant("b"), ctx).Events[0];
        await Assert.That(Ids(Applied(P(Tokens(20), ctx)))).IsEqualTo(next.EventId.ToString());
    }

    [Test]
    public async Task A_thinking_only_cluster_makes_its_first_member_canonical() {
        var ctx = Ctx();
        P(Item(Thinking), ctx);
        P(Item("""{"type":"reasoning","summary":[],"encrypted_content":"blob2"}"""), ctx);
        await Assert.That(Echoes(Applied(P(Tokens(10), ctx)))).IsEqualTo("False,True");
    }

    /// Only a produced usage finalizes the cluster: a repeat after it is a no-op until a new member
    /// opens the next cluster, and a usage-less count before it leaves the cluster open.
    [Test]
    public async Task The_cluster_finalizes_only_when_a_usage_is_produced() {
        var ctx = Ctx();
        await Assert.That(P(Tokens(10), ctx).Events).IsEmpty();

        var first = P(Assistant("a"), ctx).Events[0];
        await Assert.That(P(Msg("""{"type":"token_count","info":null}"""), ctx).Events).IsEmpty();
        await Assert.That(P(Msg("""{"type":"token_count","info":{"total_token_usage":{"input_tokens":1}}}"""), ctx).Events).IsEmpty();
        await Assert.That(Applied(P(Tokens(10), ctx)).Targets.Single().EventId).IsEqualTo(first.EventId);
        await Assert.That(P(Tokens(0), ctx).Events).IsEmpty();

        ctx.BeginBatch();
        var next = P(Assistant("b"), ctx).Events[0];
        await Assert.That(Applied(P(Tokens(20), ctx)).Targets.Single().EventId).IsEqualTo(next.EventId);
    }

    /// A turn that omits its model clears the previous one.
    [Test]
    public async Task The_usage_model_is_the_latest_turns_even_when_it_has_none() {
        var ctx = Ctx();
        P(Env("turn_context", """{"model":"gpt-5"}"""), ctx);
        P(Env("turn_context", """{"cwd":"/w"}"""), ctx);
        P(Assistant("a"), ctx);
        await Assert.That(Applied(P(Tokens(10), ctx)).Usage.HasModel).IsFalse();
    }

    /// The call id comes from the `web_search_end` before the call, however many batches apart;
    /// a re-delivered end cannot queue its id again, and a re-delivered call gets the id it had.
    [Test]
    public async Task A_web_search_takes_its_call_id_from_the_queue_across_batches() {
        var ctx  = Ctx();
        var end  = Msg("""{"type":"web_search_end","call_id":"ws_1"}""");
        var line = Item("""{"type":"web_search_call","status":"completed","action":{"type":"search","query":"q"}}""", "2026-09-04T09:00:05Z");

        P(end, ctx);
        ctx.BeginBatch();
        var e = P(line, ctx).Events;
        await Assert.That(((AssistantToolCallsGenerated)e[0].Payload).ToolCalls[0].CallId).IsEqualTo("ws_1");
        await Assert.That(((ToolResultReceived)e[1].Payload).CallId).IsEqualTo("ws_1");
        await Assert.That(e[1].Timestamp).IsEqualTo(e[0].Timestamp.AddTicks(1));
        await Assert.That(((ToolResultReceived)e[1].Payload).Timestamp.ToDateTimeOffset()).IsEqualTo(e[1].Timestamp);
        await Assert.That(e[1].RecordTimestamp).IsEqualTo("2026-09-04T09:00:05Z");

        P(end, ctx);
        var replayed = P(line, ctx).Events;
        await Assert.That(((AssistantToolCallsGenerated)replayed[0].Payload).ToolCalls[0].CallId).IsEqualTo("ws_1");

        var other = Item("""{"type":"web_search_call","status":"completed","action":{"type":"open_page","url":"u"}}""");
        var unpaired = P(other, ctx).Events;
        await Assert.That(((AssistantToolCallsGenerated)unpaired[0].Payload).ToolCalls[0].CallId)
            .IsEqualTo(TranscriptIds.CodexRecord(other).ToString("N"));
    }

    [Test]
    public async Task A_web_search_joins_the_cluster_and_its_result_does_not() {
        var ctx    = Ctx();
        var search = P(Item("""{"type":"web_search_call","status":"completed","action":{"type":"search","query":"q"}}"""), ctx).Events;
        await Assert.That(Applied(P(Tokens(10), ctx)).Targets.Single().EventId).IsEqualTo(search[0].EventId);
    }

    [Test]
    public async Task Compaction_keeps_its_history_and_lifts_out_the_encrypted_summary() {
        var e = P(Env("compacted", """{"message":"","replacement_history":[{"type":"message","role":"user"},{"type":"compaction","encrypted_content":"SUMMARY"}]}""")).Events;

        await Assert.That(e[0].EventType).IsEqualTo(CanonicalEventTypes.ContextCompacted);
        var compacted = (ContextCompactedEvent)e[0].Payload;
        await Assert.That(compacted.EncryptedContent).IsEqualTo("SUMMARY");
        await Assert.That(compacted.ReplacementHistory!.Value.GetArrayLength()).IsEqualTo(2);
        await Assert.That(compacted.ReplacementHistoryTruncated).IsFalse();
    }

    [Test]
    public async Task Compaction_over_the_budget_keeps_only_the_compaction_entries_and_records_the_cut() {
        var big = new string('a', CompactionHistoryBound.MaxBytes);
        var e   = P(Env("compacted", $$"""{"replacement_history":[{"type":"message","text":"{{big}}"},{"type":"compaction","encrypted_content":"S"}]}""")).Events;

        var compacted = (ContextCompactedEvent)e[0].Payload;
        await Assert.That(compacted.ReplacementHistoryTruncated).IsTrue();
        await Assert.That(compacted.ReplacementHistoryEntries).IsEqualTo(2);
        await Assert.That(compacted.ReplacementHistoryBytes!.Value).IsGreaterThan(CompactionHistoryBound.MaxBytes);
        await Assert.That(compacted.ReplacementHistory!.Value.GetArrayLength()).IsEqualTo(1);
        await Assert.That(compacted.EncryptedContent).IsEqualTo("S");
    }
}
