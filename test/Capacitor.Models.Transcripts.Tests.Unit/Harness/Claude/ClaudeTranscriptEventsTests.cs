using Capacitor.Models.Transcripts.Harness.Claude;
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Claude;

public class ClaudeTranscriptEventsTests {
    static readonly DateTimeOffset Received = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);
    const string Uuid = "a1b2c3d4-0000-4000-8000-000000000001";

    static ProjectionResult P(string line, int lineNumber = 1) =>
        ClaudeTranscriptEvents.Instance.Project(line, lineNumber, Received, ClaudeTranscriptEvents.Instance.CreateContext("sess", null));

    static IReadOnlyList<CanonicalEvent> E(string line, int lineNumber = 1) => P(line, lineNumber).Events;

    [Test]
    public async Task String_user_content_is_one_user_message_on_the_record_id_with_its_timestamps() {
        var e = E($$$"""{"type":"user","uuid":"{{{Uuid}}}","parentUuid":"p1","message":{"role":"user","content":"hello"},"timestamp":"2026-08-26T12:00:00Z"}""");
        await Assert.That(e).Count().IsEqualTo(1);
        await Assert.That(e[0].EventType).IsEqualTo(CanonicalEventTypes.UserMessageReceived);
        await Assert.That(((UserMessageReceived)e[0].Payload).Content).IsEqualTo("hello");
        await Assert.That(e[0].EventId).IsEqualTo(Guid.Parse(Uuid));
        await Assert.That(e[0].CausedBy).IsEqualTo("p1");
        await Assert.That(e[0].RecordTimestamp).IsEqualTo("2026-08-26T12:00:00Z");
        await Assert.That(e[0].Timestamp).IsEqualTo(new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero));
        await Assert.That(((UserMessageReceived)e[0].Payload).Timestamp.ToDateTimeOffset()).IsEqualTo(e[0].Timestamp);
    }

    [Test]
    public async Task A_record_without_uuid_gets_the_fallback_id_and_no_timestamp_uses_the_receive_time() {
        const string line = """{"type":"user","message":{"content":"x"}}""";
        var e = E(line, 7);
        await Assert.That(e[0].EventId).IsEqualTo(TranscriptIds.ClaudeFallback(7, line));
        await Assert.That(e[0].Timestamp).IsEqualTo(Received);
        await Assert.That(e[0].RecordTimestamp).IsNull();
    }

    [Test]
    public async Task Meta_sidechain_and_origin_ride_the_claude_code_extension_instead_of_being_dropped() {
        var meta = E("""{"type":"user","isMeta":true,"message":{"content":"x"}}""");
        await Assert.That(SchemaExtensions.Flag(SchemaExtensions.Slug(meta[0].Payload, "claude_code"), "is_meta")).IsTrue();

        var side = E("""{"type":"assistant","isSidechain":true,"message":{"content":[{"type":"text","text":"x"}]}}""");
        await Assert.That(SchemaExtensions.Flag(SchemaExtensions.Slug(side[0].Payload, "claude_code"), "is_sidechain")).IsTrue();

        var task = E("""{"type":"user","origin":{"kind":"task-notification"},"message":{"content":"<task-notification><summary>done</summary></task-notification>"}}""");
        await Assert.That(SchemaExtensions.Text(SchemaExtensions.Slug(task[0].Payload, "claude_code"), "origin_kind")).IsEqualTo("task-notification");
        await Assert.That(((UserMessageReceived)task[0].Payload).Content).Contains("<summary>done</summary>");

        var plain = E("""{"type":"user","message":{"content":"x"}}""");
        await Assert.That(SchemaExtensions.Slug(plain[0].Payload, "claude_code")).IsNull();
    }

    [Test]
    public async Task User_text_blocks_join_and_wrappers_are_kept_verbatim() {
        var e = E("""{"type":"user","message":{"content":[{"type":"text","text":"<system-reminder>noise</system-reminder>real"},{"type":"text","text":"more"}]}}""");
        await Assert.That(e).Count().IsEqualTo(1);
        await Assert.That(((UserMessageReceived)e[0].Payload).Content).IsEqualTo("<system-reminder>noise</system-reminder>real\nmore");
    }

    [Test]
    public async Task Tool_results_come_one_per_block_with_text_and_error_flag_and_nothing_else_from_the_record() {
        var e = E($$$"""{"type":"user","uuid":"{{{Uuid}}}","message":{"content":[{"type":"text","text":"ignored"},{"type":"tool_result","tool_use_id":"t1","content":"done","is_error":true},{"type":"tool_result","tool_use_id":"t2","content":[{"type":"text","text":"a"},{"type":"text","text":"b"}]}]}}""");
        await Assert.That(e).Count().IsEqualTo(2);
        var first = (ToolResultReceived)e[0].Payload;
        await Assert.That(first.CallId).IsEqualTo("t1");
        await Assert.That(first.Result).IsEqualTo("done");
        await Assert.That(SchemaExtensions.Flag(SchemaExtensions.Slug(first, "claude_code"), "is_error")).IsTrue();
        await Assert.That(e[0].EventId).IsEqualTo(Guid.Parse(Uuid));

        var second = (ToolResultReceived)e[1].Payload;
        await Assert.That(second.Result).IsEqualTo("a\nb");
        await Assert.That(SchemaExtensions.Slug(second, "claude_code")).IsNull();
        await Assert.That(e[1].EventId).IsEqualTo(TranscriptIds.ClaudeBlock(Guid.Parse(Uuid), 2));
    }

    [Test]
    public async Task A_tool_result_with_non_text_blocks_keeps_the_raw_json() {
        var e = E("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t","content":[{"type":"image","source":{}}]}]}}""");
        await Assert.That(((ToolResultReceived)e[0].Payload).Result).IsEqualTo("""[{"type":"image","source":{}}]""");
    }

    [Test]
    public async Task Assistant_blocks_map_in_order_with_the_record_id_first_and_block_siblings_after() {
        var line = $$$"""{"type":"assistant","uuid":"{{{Uuid}}}","timestamp":"2026-08-26T12:00:01Z","message":{"model":"claude-fable-5","content":[{"type":"thinking","thinking":"hmm","signature":"sig"},{"type":"text","text":"Hi"},{"type":"tool_use","id":"toolu_1","name":"Bash","input":{"command":"ls"}}]}}""";
        var e = E(line);

        await Assert.That(e).Count().IsEqualTo(3);
        var thinking = (AssistantThinkingGenerated)e[0].Payload;
        await Assert.That(thinking.Content).IsEqualTo("hmm");
        await Assert.That(thinking.Signature).IsEqualTo("sig");
        await Assert.That(thinking.Encrypted).IsFalse();
        await Assert.That(e[0].EventId).IsEqualTo(Guid.Parse(Uuid));

        await Assert.That(((AssistantTextGenerated)e[1].Payload).Content).IsEqualTo("Hi");
        await Assert.That(e[1].EventId).IsEqualTo(TranscriptIds.ClaudeBlock(Guid.Parse(Uuid), 1));

        var call = ((AssistantToolCallsGenerated)e[2].Payload).ToolCalls[0];
        await Assert.That(call.CallId).IsEqualTo("toolu_1");
        await Assert.That(call.ToolName).IsEqualTo("Bash");
        await Assert.That(call.Arguments.Fields["command"].StringValue).IsEqualTo("ls");
        await Assert.That(e[2].EventId).IsEqualTo(TranscriptIds.ClaudeBlock(Guid.Parse(Uuid), 2));
        await Assert.That(e[2].RecordTimestamp).IsEqualTo("2026-08-26T12:00:01Z");
    }

    [Test]
    public async Task Non_object_and_absent_tool_inputs_are_wrapped_into_an_object() {
        foreach (var (input, kind) in new[] { ("[1,2]", Value.KindOneofCase.ListValue), ("\"s\"", Value.KindOneofCase.StringValue), ("null", Value.KindOneofCase.NullValue) }) {
            var e = E($$$"""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t","name":"X","input":{{{input}}}}]}}""");
            var args = ((AssistantToolCallsGenerated)e[0].Payload).ToolCalls[0].Arguments;
            await Assert.That(args.Fields["input"].KindCase).IsEqualTo(kind).Because(input);
        }
        var absent = E("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t","name":"X"}]}}""");
        await Assert.That(((AssistantToolCallsGenerated)absent[0].Payload).ToolCalls[0].Arguments.Fields.Count).IsEqualTo(0);
    }

    /// The persisted kind comes from the table the chat lane stamps, so an import and the chat never
    /// disagree. The name alone decides — a `Bash` reading a file is still `execute` — and a tool the
    /// table does not list, or a nameless call, is a PRESENT `other`: the key being there is how a
    /// consumer tells "none of these" from the absence a lane with no table leaves behind.
    [Test]
    public async Task Tool_calls_carry_a_vendor_neutral_kind_and_an_unlisted_tool_is_a_present_other() {
        foreach (var (block, kind) in new[] {
            ("""{"type":"tool_use","id":"t","name":"Read","input":{"file_path":"a.rs"}}""",           AcpToolKind.Read),
            ("""{"type":"tool_use","id":"t","name":"Edit","input":{"file_path":"a.rs"}}""",           AcpToolKind.Edit),
            ("""{"type":"tool_use","id":"t","name":"Bash","input":{"command":"cat a.rs"}}""",         AcpToolKind.Execute),
            ("""{"type":"tool_use","id":"t","name":"Grep","input":{"pattern":"x"}}""",                AcpToolKind.Search),
            ("""{"type":"tool_use","id":"t","name":"WebFetch","input":{"url":"https://x"}}""",        AcpToolKind.Fetch),
            ("""{"type":"tool_use","id":"t","name":"Task","input":{"prompt":"go"}}""",                AcpToolKind.Other),
            ("""{"type":"tool_use","id":"t","name":"mcp__linear__get_issue","input":{"id":"1"}}""",   AcpToolKind.Other),
            ("""{"type":"tool_use","id":"t","input":{}}""",                                           AcpToolKind.Other),
        }) {
            var e    = E($$$"""{"type":"assistant","message":{"content":[{{{block}}}]}}""");
            var call = ((AssistantToolCallsGenerated)e[0].Payload).ToolCalls[0];
            await Assert.That(call.HasToolKind).IsTrue().Because(block);
            await Assert.That(call.ToolKind).IsEqualTo(kind).Because(block);
        }
    }

    [Test]
    public async Task Empty_thinking_stays_a_thinking_event_with_empty_content() {
        var e = E("""{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"","signature":"abc"}]}}""");
        await Assert.That(((AssistantThinkingGenerated)e[0].Payload).Content).IsEqualTo("");
    }

    [Test]
    public async Task Deferred_tools_injection_and_empty_text_emit_nothing() {
        await Assert.That(E("""{"type":"user","message":{"content":"<available-deferred-tools>x"}}""")).IsEmpty();
        await Assert.That(E("""{"type":"user","message":{"content":[{"type":"text","text":"  <available-deferred-tools>x"}]}}""")).IsEmpty();
        await Assert.That(E("""{"type":"assistant","message":{"content":[{"type":"text","text":""}]}}""")).IsEmpty();
    }

    [Test]
    public async Task Every_other_record_type_is_ignored_not_rejected() {
        foreach (var type in new[] { "summary", "system", "file-history-snapshot", "file-history-delta", "mode", "permission-mode", "last-prompt", "ai-title", "atis-latch", "worktree-state", "queue-operation", "progress", "unknown-future" }) {
            var r = P($$$"""{"type":"{{{type}}}","message":{"content":"x"}}""");
            await Assert.That(r.Events).IsEmpty().Because(type);
            await Assert.That(r.Rejected).IsNull().Because(type);
        }
        await Assert.That(P("""{"type":"user","message":{"content":42}}""").Events).IsEmpty();
        await Assert.That(P("""{"type":"assistant","message":{"content":[{"type":"text","text":7}]}}""").Events).IsEmpty();
    }

    [Test]
    public async Task An_attachment_that_is_not_a_task_notification_queued_command_is_ignored() {
        foreach (var line in new[] {
            """{"type":"attachment","message":{"content":"x"}}""",
            """{"type":"attachment","attachment":{"type":"queued_command","prompt":"go","commandMode":"prompt"}}""",
            """{"type":"attachment","attachment":{"type":"prompt_snapshot","prompt":"x","commandMode":"task-notification"}}""",
            """{"type":"attachment","attachment":{"type":"file","prompt":"x","commandMode":"task-notification"}}""",
            """{"type":"attachment","attachment":{"type":"queued_command","commandMode":"task-notification"}}""",
        }) {
            var r = P(line);
            await Assert.That(r.Events).IsEmpty().Because(line);
            await Assert.That(r.Rejected).IsNull().Because(line);
        }
    }

    [Test]
    public async Task A_queued_command_task_notification_attachment_projects_one_user_message() {
        var line = $$$"""{"parentUuid":"p1","isSidechain":false,"attachment":{"type":"queued_command","prompt":"<task-notification>\n<task-id>a1</task-id>\n<tool-use-id>toolu_1</tool-use-id>\n<status>completed</status>\n<summary>Agent \"X\" finished</summary>\n</task-notification>","source_uuid":"s1","commandMode":"task-notification","timestamp":"2026-09-17T11:53:57.948Z"},"type":"attachment","uuid":"{{{Uuid}}}","timestamp":"2026-09-17T11:53:58.000Z"}""";
        var e = E(line);
        await Assert.That(e).Count().IsEqualTo(1);
        await Assert.That(e[0].EventType).IsEqualTo(CanonicalEventTypes.UserMessageReceived);
        await Assert.That(((UserMessageReceived)e[0].Payload).Content).IsEqualTo("<task-notification>\n<task-id>a1</task-id>\n<tool-use-id>toolu_1</tool-use-id>\n<status>completed</status>\n<summary>Agent \"X\" finished</summary>\n</task-notification>");
        await Assert.That(e[0].EventId).IsEqualTo(Guid.Parse(Uuid));
        await Assert.That(e[0].CausedBy).IsEqualTo("p1");
        await Assert.That(e[0].RecordTimestamp).IsEqualTo("2026-09-17T11:53:58.000Z");

        var slug = SchemaExtensions.Slug(e[0].Payload, "claude_code");
        await Assert.That(SchemaExtensions.Text(slug, "origin_kind")).IsEqualTo("task-notification");
        await Assert.That(slug!.Fields.ContainsKey("is_meta")).IsFalse();
    }

    [Test]
    public async Task A_sidechain_attachment_notification_carries_is_sidechain() {
        var line = """{"type":"attachment","isSidechain":true,"attachment":{"type":"queued_command","prompt":"<task-notification></task-notification>","commandMode":"task-notification"}}""";
        var e = E(line);
        await Assert.That(SchemaExtensions.Flag(SchemaExtensions.Slug(e[0].Payload, "claude_code"), "is_sidechain")).IsTrue();
    }

    [Test]
    public async Task A_uuid_matters_only_on_a_record_the_projection_reads() {
        var ignored = P("""{"type":"progress","uuid":"bad","message":{"content":"x"}}""");
        await Assert.That(ignored.Rejected).IsNull();
        await Assert.That(ignored.Events).IsEmpty();

        var numeric = P("""{"type":"user","uuid":42,"message":{"content":"x"}}""");
        await Assert.That(numeric.Rejected).IsNotNull();
        await Assert.That(numeric.Events).IsEmpty();
    }

    [Test]
    public async Task Malformed_lines_are_rejected_with_a_reason_and_emit_nothing() {
        foreach (var line in new[] { "", "   ", "not json", "[1,2]", "\"s\"", $$$"""{"type":"user","uuid":"not-a-guid","message":{"content":"x"}}""" }) {
            var r = P(line);
            await Assert.That(r.Rejected).IsNotNull().Because(line);
            await Assert.That(r.Events).IsEmpty().Because(line);
        }
    }

    [Test]
    public async Task Events_of_one_record_never_share_an_extension_struct() {
        var e = E("""{"type":"assistant","isSidechain":true,"message":{"content":[{"type":"text","text":"a"},{"type":"text","text":"b"}]}}""");
        await Assert.That(e).Count().IsEqualTo(2);
        var first  = SchemaExtensions.Slug(e[0].Payload, "claude_code");
        var second = SchemaExtensions.Slug(e[1].Payload, "claude_code");
        await Assert.That(first).IsNotNull();
        await Assert.That(second).IsNotNull();
        await Assert.That(ReferenceEquals(first, second)).IsFalse();
    }

    const string LaunchLine = """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_A","content":[{"type":"text","text":"Async agent launched successfully."}]}]},"toolUseResult":{"isAsync":true,"status":"async_launched","agentId":"a9f262478e032f427","description":"Map desktop chat UI surfaces","prompt":"go"}}""";

    [Test]
    public async Task A_single_result_lines_toolUseResult_object_rides_the_extension_unchanged() {
        var e = E(LaunchLine);
        await Assert.That(e).Count().IsEqualTo(1);
        var slug = SchemaExtensions.Slug(e[0].Payload, "claude_code");
        await Assert.That(slug).IsNotNull();
        var result = slug!.Fields["tool_use_result"].StructValue;
        await Assert.That(result.Fields["status"].StringValue).IsEqualTo("async_launched");
        await Assert.That(result.Fields["agentId"].StringValue).IsEqualTo("a9f262478e032f427");
        await Assert.That(result.Fields["isAsync"].BoolValue).IsTrue();
        await Assert.That(result.Fields["description"].StringValue).IsEqualTo("Map desktop chat UI surfaces");
        await Assert.That(result.Fields["prompt"].StringValue).IsEqualTo("go");
        await Assert.That(result.Fields.Count).IsEqualTo(5);
        await Assert.That(((ToolResultReceived)e[0].Payload).Result).IsEqualTo("Async agent launched successfully.");
    }

    [Test]
    public async Task A_line_without_toolUseResult_or_with_a_non_object_one_adds_no_key() {
        var none = E("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]}}""");
        await Assert.That(SchemaExtensions.Slug(none[0].Payload, "claude_code")).IsNull();

        foreach (var value in new[] { "\"text\"", "42", "[1]", "null", "true" }) {
            var e = E($$$"""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]},"toolUseResult":{{{value}}}}""");
            await Assert.That(SchemaExtensions.Slug(e[0].Payload, "claude_code")).IsNull().Because(value);
        }

        var error = E("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"x","is_error":true}]},"toolUseResult":"Error: x"}""");
        var slug = SchemaExtensions.Slug(error[0].Payload, "claude_code");
        await Assert.That(SchemaExtensions.Flag(slug, "is_error")).IsTrue();
        await Assert.That(slug!.Fields.ContainsKey("tool_use_result")).IsFalse();
    }

    [Test]
    public async Task A_multi_result_line_puts_toolUseResult_on_no_result() {
        var e = E("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"a"},{"type":"tool_result","tool_use_id":"t2","content":"b"}]},"toolUseResult":{"status":"async_launched","agentId":"x"}}""");
        await Assert.That(e).Count().IsEqualTo(2);
        await Assert.That(SchemaExtensions.Slug(e[0].Payload, "claude_code")).IsNull();
        await Assert.That(SchemaExtensions.Slug(e[1].Payload, "claude_code")).IsNull();
    }

    /// The ids hash the record id and block index only, so the extension can never move one.
    [Test]
    public async Task The_tool_use_result_extension_leaves_event_ids_alone() {
        var withUuid = E($$$"""{"type":"user","uuid":"{{{Uuid}}}","message":{"content":[{"type":"text","text":"x"},{"type":"tool_result","tool_use_id":"t1","content":"ok"}]},"toolUseResult":{"status":"async_launched","agentId":"x"}}""");
        await Assert.That(withUuid[0].EventId).IsEqualTo(Guid.Parse(Uuid));
        await Assert.That(SchemaExtensions.Slug(withUuid[0].Payload, "claude_code")).IsNotNull();

        const string fallback = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]},"toolUseResult":{"status":"async_launched","agentId":"x"}}""";
        await Assert.That(E(fallback, 9)[0].EventId).IsEqualTo(TranscriptIds.ClaudeFallback(9, fallback));
    }
}
