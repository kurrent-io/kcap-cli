namespace Capacitor.Cli.Core.Tests.Unit.Harness.Claude;

/// The chat-level view of Claude records: what the Chat tab shows for each record shape.
public class ClaudeChatRulesTests {
    static readonly DateTimeOffset Received = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);

    static IReadOnlyList<AcpEventEnvelope> P(string line) {
        var chat = TranscriptChat.For("claude")!;
        return chat.Project(line, 1, Received, chat.CreateContext("a1", null));
    }

    [Test]
    [Arguments("")]
    [Arguments(" inspect the queue ")]
    public async Task Hidden_slash_commands_acknowledge_the_submitted_command_and_arguments(string args) {
        var chat = TranscriptChat.For("claude")!;
        var result = chat.ProjectWithInputs($$$"""{"type":"user","message":{"content":"<command-name>/review</command-name><command-message>review</command-message><command-args>{{{args}}}</command-args>"}}""", 1, Received, chat.CreateContext("a1", null));
        await Assert.That(result.Envelopes).IsEmpty();
        await Assert.That(result.SubmittedInputs).IsEquivalentTo(new[] { args.Trim().Length == 0 ? "/review" : $"/review {args.Trim()}" });
    }

    [Test]
    [Arguments("\"isMeta\":true")]
    [Arguments("\"isSidechain\":true")]
    [Arguments("\"origin\":{\"kind\":\"task-notification\"}")]
    public async Task Injected_command_wrappers_never_acknowledge_user_input(string flags) {
        var chat = TranscriptChat.For("claude")!;
        var result = chat.ProjectWithInputs($$$"""{"type":"user",{{{flags}}},"message":{"content":"<command-name>/clear</command-name><local-command-stdout>ok</local-command-stdout>"}}""", 1, Received, chat.CreateContext("a1", null));
        await Assert.That(result.SubmittedInputs).IsEmpty();
        await Assert.That(result.Envelopes.Any(e => e.Kind == AcpEventKind.UserMessage)).IsFalse();
    }

    [Test]
    public async Task A_bang_command_shows_the_command_and_acknowledges_it() {
        var chat = TranscriptChat.For("claude")!;
        var result = chat.ProjectWithInputs("""{"type":"user","message":{"content":"<bash-input>kubectl get pods</bash-input>"}}""", 1, Received, chat.CreateContext("a1", null));
        await Assert.That(result.Envelopes).Count().IsEqualTo(1);
        await Assert.That(result.Envelopes[0].Kind).IsEqualTo(AcpEventKind.UserMessage);
        await Assert.That(result.Envelopes[0].Text).IsEqualTo("! kubectl get pods");
        await Assert.That(result.SubmittedInputs).IsEquivalentTo(new[] { "!kubectl get pods" });
    }

    [Test]
    public async Task Bang_output_is_a_system_note_and_a_blank_one_is_dropped() {
        var shown = P("""{"type":"user","message":{"content":"<bash-stdout>NAME\nweb</bash-stdout><bash-stderr></bash-stderr>"}}""");
        await Assert.That(shown).Count().IsEqualTo(1);
        await Assert.That(shown[0].Kind).IsEqualTo(AcpEventKind.SystemNote);
        await Assert.That(shown[0].Text).IsEqualTo("NAME\nweb");

        var stderr = P("""{"type":"user","message":{"content":"<bash-stdout></bash-stdout><bash-stderr>denied</bash-stderr>"}}""");
        await Assert.That(stderr).Count().IsEqualTo(1);
        await Assert.That(stderr[0].Kind).IsEqualTo(AcpEventKind.SystemNote);
        await Assert.That(stderr[0].Text).IsEqualTo("denied");

        var both = P("""{"type":"user","message":{"content":"<bash-stdout>ok</bash-stdout><bash-stderr>warn</bash-stderr>"}}""");
        await Assert.That(both[0].Text).IsEqualTo("ok\nwarn");

        await Assert.That(P("""{"type":"user","message":{"content":"<bash-stdout></bash-stdout><bash-stderr></bash-stderr>"}}""")).IsEmpty();
    }

    [Test]
    public async Task Quoted_bash_tags_stay_a_user_message_and_do_not_acknowledge_a_bang() {
        var chat = TranscriptChat.For("claude")!;
        var result = chat.ProjectWithInputs("""{"type":"user","message":{"content":"see <bash-input>ls</bash-input> below"}}""", 1, Received, chat.CreateContext("a1", null));
        await Assert.That(result.Envelopes).Count().IsEqualTo(1);
        await Assert.That(result.Envelopes[0].Kind).IsEqualTo(AcpEventKind.UserMessage);
        await Assert.That(result.Envelopes[0].Text).IsEqualTo("see <bash-input>ls</bash-input> below");
        await Assert.That(result.SubmittedInputs).IsEquivalentTo(new[] { "see <bash-input>ls</bash-input> below" });
    }

    [Test]
    public async Task String_user_content_is_one_user_message_with_its_timestamp() {
        var e = P("""{"type":"user","message":{"role":"user","content":"hello"},"timestamp":"2026-08-26T12:00:00Z"}""");
        await Assert.That(e).Count().IsEqualTo(1);
        await Assert.That(e[0].Kind).IsEqualTo(AcpEventKind.UserMessage);
        await Assert.That(e[0].Text).IsEqualTo("hello");
        await Assert.That(e[0].TimestampIso).IsEqualTo("2026-08-26T12:00:00Z");
    }

    [Test]
    public async Task Meta_and_sidechain_records_project_to_nothing() {
        await Assert.That(P("""{"type":"user","isMeta":true,"message":{"content":"x"}}""")).IsEmpty();
        await Assert.That(P("""{"type":"user","isSidechain":true,"message":{"content":"x"}}""")).IsEmpty();
        await Assert.That(P("""{"type":"assistant","isSidechain":true,"message":{"content":[{"type":"text","text":"x"}]}}""")).IsEmpty();
    }

    [Test]
    public async Task Wrappers_are_stripped_and_a_blank_remainder_is_not_emitted() {
        var stripped = P("""{"type":"user","message":{"content":[{"type":"text","text":"<system-reminder>\nnoise\n</system-reminder>real"}]}}""");
        await Assert.That(stripped).Count().IsEqualTo(1);
        await Assert.That(stripped[0].Text).IsEqualTo("real");

        var onlyWrappers = P("""{"type":"user","message":{"content":[{"type":"text","text":"<command-name>/clear</command-name><local-command-stdout>ok</local-command-stdout>"}]}}""");
        await Assert.That(onlyWrappers).IsEmpty();
    }

    [Test]
    public async Task Tool_results_carry_string_or_block_content_capped_and_flag_errors() {
        var str = P("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"done","is_error":true}]}}""");
        await Assert.That(str[0].Kind).IsEqualTo(AcpEventKind.ToolResult);
        await Assert.That(str[0].ToolCallId).IsEqualTo("t1");
        await Assert.That(str[0].ToolResult).IsEqualTo("done");
        await Assert.That(str[0].ToolIsError).IsTrue();

        var blocks = P("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t2","content":[{"type":"text","text":"a"},{"type":"text","text":"b"}]}]}}""");
        await Assert.That(blocks[0].ToolResult).IsEqualTo("a\nb");
        await Assert.That(blocks[0].ToolIsError).IsFalse();

        var big = new string('x', 5000);
        var capped = P($$$"""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t3","content":"{{{big}}}"}]}}""");
        await Assert.That(capped[0].ToolResult!.Length).IsEqualTo(4096);
    }

    [Test]
    public async Task Assistant_blocks_map_to_text_thinking_and_tool_call() {
        var line = """{"type":"assistant","timestamp":"2026-08-26T12:00:01Z","message":{"model":"claude-fable-5","content":[{"type":"thinking","thinking":"hmm"},{"type":"text","text":"Hi"},{"type":"tool_use","id":"toolu_1","name":"Bash","input":{"command":"ls"}}]}}""";
        var e = P(line);

        await Assert.That(e).Count().IsEqualTo(3);
        await Assert.That(e[0].Kind).IsEqualTo(AcpEventKind.AssistantThinking);
        await Assert.That(e[0].Text).IsEqualTo("hmm");
        await Assert.That(e[1].Kind).IsEqualTo(AcpEventKind.AssistantText);
        await Assert.That(e[1].Text).IsEqualTo("Hi");
        await Assert.That(e[2].Kind).IsEqualTo(AcpEventKind.ToolCall);
        await Assert.That(e[2].ToolCallId).IsEqualTo("toolu_1");
        await Assert.That(e[2].ToolName).IsEqualTo("Bash");
        await Assert.That(e[2].ToolInputJson).IsEqualTo("""{"command":"ls"}""");
        await Assert.That(e[2].TimestampIso).IsEqualTo("2026-08-26T12:00:01Z");
    }

    [Test]
    public async Task Encrypted_thinking_and_non_object_inputs_are_normalized() {
        var enc = P("""{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"","signature":"abc"}]}}""");
        await Assert.That(enc[0].ThinkingEncrypted).IsTrue();

        foreach (var (input, expected) in new[] {
            ("[1,2]", """{"input":[1,2]}"""),
            ("\"s\"", """{"input":"s"}"""),
            ("null", """{"input":null}"""),
        }) {
            var e = P($$$"""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t","name":"X","input":{{{input}}}}]}}""");
            await Assert.That(e[0].ToolInputJson).IsEqualTo(expected);
        }
    }

    [Test]
    public async Task Every_other_record_type_and_malformed_input_project_to_nothing() {
        foreach (var type in new[] { "summary", "system", "file-history-snapshot", "file-history-delta", "mode", "permission-mode", "last-prompt", "ai-title", "atis-latch", "worktree-state", "queue-operation", "progress", "unknown-future" })
            await Assert.That(P($$$"""{"type":"{{{type}}}","message":{"content":"x"}}""")).IsEmpty().Because(type);

        await Assert.That(P("""{"type":"attachment","message":{"content":"x"}}""")).IsEmpty();
        await Assert.That(P("""{"type":"attachment","attachment":{"type":"queued_command","prompt":"go","commandMode":"prompt"}}""")).IsEmpty();

        await Assert.That(P("not json")).IsEmpty();
        await Assert.That(P("[1,2]")).IsEmpty();
        await Assert.That(P("""{"type":"user","message":{"content":42}}""")).IsEmpty();
        await Assert.That(P("""{"type":"assistant","message":{"content":[{"type":"text","text":7}]}}""")).IsEmpty();
    }

    /// A record Claude Code injects for a finished background task is system-attributed, never
    /// the user's words: its summary leads in bold and its result follows as markdown.
    [Test]
    public async Task A_task_notification_projects_to_a_system_note_of_summary_and_result() {
        var line = """{"type":"user","origin":{"kind":"task-notification"},"promptSource":"system","message":{"role":"user","content":"<task-notification>\n<task-id>k1</task-id>\n<status>completed</status>\n<summary>Agent \"Review Task 15\" finished</summary>\n<result>\n## Findings\n\nNone.\n</result>\n</task-notification>"}}""";
        var note = P(line);
        await Assert.That(note).Count().IsEqualTo(1);
        await Assert.That(note[0].Kind).IsEqualTo(AcpEventKind.SystemNote);
        await Assert.That(note[0].Text).IsEqualTo("**Agent \"Review Task 15\" finished**\n\n## Findings\n\nNone.");

        var bare = P("""{"type":"user","origin":{"kind":"task-notification"},"message":{"content":"<task-notification>\n<summary>MCP task done.</summary>\n</task-notification>"}}""");
        await Assert.That(bare[0].Text).IsEqualTo("**MCP task done.**");

        var human = P("""{"type":"user","origin":{"kind":"human"},"promptSource":"typed","message":{"content":"hello"}}""");
        await Assert.That(human[0].Kind).IsEqualTo(AcpEventKind.UserMessage);
    }

    /// Pins the vendor-neutral kind beside the raw name. Every tool gets one — an unlisted tool is
    /// `other`, never null, so a null kind can only mean a lane that classifies nothing.
    [Test]
    public async Task Every_tool_call_carries_a_kind_and_an_unlisted_tool_is_other() {
        foreach (var (name, kind) in new[] {
            ("Read", AcpToolKind.Read), ("NotebookRead", AcpToolKind.Read),
            ("Edit", AcpToolKind.Edit), ("MultiEdit", AcpToolKind.Edit), ("Write", AcpToolKind.Edit), ("NotebookEdit", AcpToolKind.Edit),
            ("Bash", AcpToolKind.Execute), ("BashOutput", AcpToolKind.Execute), ("KillShell", AcpToolKind.Execute),
            ("Grep", AcpToolKind.Search), ("Glob", AcpToolKind.Search), ("LS", AcpToolKind.Search),
            ("WebFetch", AcpToolKind.Fetch), ("WebSearch", AcpToolKind.Fetch),
            ("Task", AcpToolKind.Other), ("Skill", AcpToolKind.Other), ("TodoWrite", AcpToolKind.Other),
            ("mcp__linear__get_issue", AcpToolKind.Other), ("SomeFutureTool", AcpToolKind.Other),
        }) {
            var e = P($$$"""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t","name":"{{{name}}}","input":{}}]}}""");
            await Assert.That(e[0].ToolName).IsEqualTo(name);
            await Assert.That(e[0].ToolKind).IsEqualTo(kind).Because(name);
        }
    }

    /// A shell call keeps `execute` whatever the command reads like: Claude has real Read and Grep
    /// tools, so classifying the command would misreport the tool the model actually chose.
    [Test]
    public async Task A_bash_call_that_reads_a_file_is_still_execute() {
        var e = P("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t","name":"Bash","input":{"command":"sed -n '1,20p' src/Program.cs"}}]}}""");
        await Assert.That(e[0].ToolKind).IsEqualTo(AcpToolKind.Execute);
    }

    static ChatProjectionResult R(string line) {
        var chat = TranscriptChat.For("claude")!;
        return chat.ProjectWithInputs(line, 1, Received, chat.CreateContext("a1", null));
    }

    const string AgentCall = """{"type":"assistant","timestamp":"2026-09-17T10:00:00Z","message":{"content":[{"type":"tool_use","id":"toolu_A","name":"Agent","input":{"description":"Map desktop chat UI surfaces","prompt":"go","subagent_type":"Explore"}}]}}""";
    const string LaunchResult = """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_A","content":[{"type":"text","text":"Async agent launched successfully."}]}]},"toolUseResult":{"isAsync":true,"status":"async_launched","agentId":"a9f262478e032f427","description":"Map desktop chat UI surfaces","prompt":"go"}}""";
    const string NotificationText = "<task-notification>\n<task-id>a9f262478e032f427</task-id>\n<tool-use-id>toolu_A</tool-use-id>\n<output-file>/tmp/x.output</output-file>\n<status>completed</status>\n<summary>Agent \"Map desktop chat UI surfaces\" finished</summary>\n</task-notification>";

    static string Notification(bool originKind, string status = "completed", string flags = "") =>
        $$$"""{"type":"user","timestamp":"2026-09-17T10:05:00Z",{{{(originKind ? "\"origin\":{\"kind\":\"task-notification\"}," : "")}}}{{{flags}}}"message":{"role":"user","content":"{{{NotificationText.Replace("\n", "\\n").Replace("\"", "\\\"").Replace("completed", status)}}}"}}""";

    [Test]
    public async Task Started_for_Agent_and_Task_calls_names_the_type_and_falls_back_to_agent() {
        var agent = R(AgentCall);
        await Assert.That(agent.Envelopes).Count().IsEqualTo(1);
        var started = (SubagentSignal.Started)agent.Subagents.Single();
        await Assert.That(started.CallId).IsEqualTo("toolu_A");
        await Assert.That(started.Name).IsEqualTo("Explore");
        await Assert.That(started.Description).IsEqualTo("Map desktop chat UI surfaces");
        await Assert.That(started.At).IsEqualTo(new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero));

        var task = R("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_B","name":"Task","input":{"description":"Review","prompt":"go"}}]}}""");
        var bare = (SubagentSignal.Started)task.Subagents.Single();
        await Assert.That(bare.CallId).IsEqualTo("toolu_B");
        await Assert.That(bare.Name).IsEqualTo("agent");
        await Assert.That(bare.Description).IsEqualTo("Review");
    }

    [Test]
    public async Task No_signal_for_a_sidechain_call_a_sidechain_notification_or_any_other_tool() {
        await Assert.That(R(AgentCall.Replace("\"type\":\"assistant\",", "\"type\":\"assistant\",\"isSidechain\":true,")).Subagents).IsEmpty();
        await Assert.That(R(Notification(originKind: true, flags: "\"isSidechain\":true,")).Subagents).IsEmpty();
        await Assert.That(R(LaunchResult.Replace("\"type\":\"user\",", "\"type\":\"user\",\"isSidechain\":true,")).Subagents).IsEmpty();
        await Assert.That(R("""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t","name":"Bash","input":{"command":"ls","subagent_type":"Explore"}}]}}""").Subagents).IsEmpty();
    }

    [Test]
    public async Task Detached_with_the_agent_id_only_for_an_async_launched_result() {
        var launch = R(LaunchResult);
        await Assert.That(launch.Envelopes.Single().Kind).IsEqualTo(AcpEventKind.ToolResult);
        var detached = (SubagentSignal.Detached)launch.Subagents.Single();
        await Assert.That(detached.CallId).IsEqualTo("toolu_A");
        await Assert.That(detached.AgentId).IsEqualTo("a9f262478e032f427");

        await Assert.That(R("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_A","content":"done"}]},"toolUseResult":{"status":"completed","agentId":"a9f262478e032f427"}}""").Subagents).IsEmpty();
        await Assert.That(R("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_A","content":"done"}]},"toolUseResult":{"status":"async_launched"}}""").Subagents).IsEmpty();
        await Assert.That(R("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_A","content":"done"}]}}""").Subagents).IsEmpty();
    }

    /// Runs twice: identified by origin_kind as the local leaf writes it, and by text alone as the
    /// server's events present it. Both yield the note row, the finish and no submitted input.
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Finished_from_a_notification_keyed_by_both_ids_and_failed_unless_completed(bool originKind) {
        var done = R(Notification(originKind));
        await Assert.That(done.Envelopes.Single().Kind).IsEqualTo(AcpEventKind.SystemNote);
        await Assert.That(done.Envelopes.Single().Text).IsEqualTo("**Agent \"Map desktop chat UI surfaces\" finished**");
        await Assert.That(done.SubmittedInputs).IsEmpty();
        var finished = (SubagentSignal.Finished)done.Subagents.Single();
        await Assert.That(finished.CallId).IsEqualTo("toolu_A");
        await Assert.That(finished.AgentId).IsEqualTo("a9f262478e032f427");
        await Assert.That(finished.Outcome).IsEqualTo(SubagentOutcome.Done);
        await Assert.That(finished.At).IsEqualTo(new DateTimeOffset(2026, 9, 17, 10, 5, 0, TimeSpan.Zero));

        var failed = (SubagentSignal.Finished)R(Notification(originKind, status: "killed")).Subagents.Single();
        await Assert.That(failed.Outcome).IsEqualTo(SubagentOutcome.Failed);
    }

    /// Claude Code delivers a notification that lands mid-turn as a queued_command attachment
    /// rather than the user line the rules above read; the leaf projects it as that line, so it
    /// finishes the subagent and shows its note the same way.
    [Test]
    public async Task An_attachment_delivered_notification_finishes_the_subagent_and_shows_its_note() {
        var line = """{"type":"attachment","attachment":{"type":"queued_command","commandMode":"task-notification","prompt":"<task-notification>\n<task-id>a8c6e38f425550515</task-id>\n<tool-use-id>toolu_A</tool-use-id>\n<status>completed</status>\n<summary>Agent \"Trace subagent data to desktop\" finished</summary>\n</task-notification>"}}""";
        var result = R(line);
        await Assert.That(result.Envelopes.Single().Kind).IsEqualTo(AcpEventKind.SystemNote);
        await Assert.That(result.Envelopes.Single().Text).IsEqualTo("**Agent \"Trace subagent data to desktop\" finished**");
        var finished = (SubagentSignal.Finished)result.Subagents.Single();
        await Assert.That(finished.CallId).IsEqualTo("toolu_A");
        await Assert.That(finished.AgentId).IsEqualTo("a8c6e38f425550515");
        await Assert.That(finished.Outcome).IsEqualTo(SubagentOutcome.Done);

        var prompt = R("""{"type":"attachment","attachment":{"type":"queued_command","commandMode":"prompt","prompt":"go do x"}}""");
        await Assert.That(prompt.Envelopes).IsEmpty();
        await Assert.That(prompt.Subagents).IsEmpty();
    }

    /// Meta hides a notification's row on either delivery: the flag rides the attachment as it
    /// rides the user line, and the finish lands either way.
    [Test]
    public async Task A_meta_attachment_notification_yields_its_finish_but_neither_row_nor_input() {
        var meta = R("""{"type":"attachment","isMeta":true,"attachment":{"type":"queued_command","commandMode":"task-notification","prompt":"<task-notification>\n<task-id>a1</task-id>\n<tool-use-id>toolu_A</tool-use-id>\n<status>completed</status>\n<summary>Agent \"X\" finished</summary>\n</task-notification>"}}""");
        await Assert.That(meta.Envelopes).IsEmpty();
        await Assert.That(meta.SubmittedInputs).IsEmpty();
        await Assert.That(meta.Subagents.Single()).IsTypeOf<SubagentSignal.Finished>();
    }

    [Test]
    public async Task A_notification_marked_meta_yields_its_finish_but_neither_row_nor_input() {
        var meta = R(Notification(originKind: true, flags: "\"isMeta\":true,"));
        await Assert.That(meta.Envelopes).IsEmpty();
        await Assert.That(meta.SubmittedInputs).IsEmpty();
        await Assert.That(meta.Subagents.Single()).IsTypeOf<SubagentSignal.Finished>();
    }

    [Test]
    public async Task A_notification_recognised_by_text_alone_is_not_a_user_turn() {
        var byText = R(Notification(originKind: false));
        await Assert.That(byText.Envelopes.Single().Kind).IsEqualTo(AcpEventKind.SystemNote);
        await Assert.That(byText.SubmittedInputs).IsEmpty();
        var indented = R("""{"type":"user","message":{"content":"  \n<task-notification>\n<summary>done</summary>\n</task-notification>"}}""");
        await Assert.That(indented.Envelopes.Single().Kind).IsEqualTo(AcpEventKind.SystemNote);
        await Assert.That(indented.Subagents).IsEmpty();
        var mention = R("""{"type":"user","message":{"content":"what does <task-notification> mean?"}}""");
        await Assert.That(mention.Envelopes.Single().Kind).IsEqualTo(AcpEventKind.UserMessage);
        await Assert.That(mention.SubmittedInputs).IsEquivalentTo(new[] { "what does <task-notification> mean?" });
    }

    [Test]
    public async Task Stopped_from_a_TaskStop_success_result_and_nothing_from_a_TaskOutput_probe() {
        var stop = R("""{"type":"user","timestamp":"2026-09-17T10:07:00Z","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_S","content":"Successfully stopped task: a9f262478e032f427"}]},"toolUseResult":{"task_id":"a9f262478e032f427","task_type":"local_agent","message":"Successfully stopped task: a9f262478e032f427"}}""");
        var stopped = (SubagentSignal.Finished)stop.Subagents.Single();
        await Assert.That(stopped.CallId).IsNull();
        await Assert.That(stopped.AgentId).IsEqualTo("a9f262478e032f427");
        await Assert.That(stopped.Outcome).IsEqualTo(SubagentOutcome.Stopped);
        await Assert.That(stopped.At).IsEqualTo(new DateTimeOffset(2026, 9, 17, 10, 7, 0, TimeSpan.Zero));

        var probe = R("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_O","content":"…"}]},"toolUseResult":{"task_id":"a9f262478e032f427","task_type":"local_agent","message":"Task output (last 10 lines)","retrieval_status":"partial"}}""");
        await Assert.That(probe.Subagents).IsEmpty();
        var shell = R("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_S","content":"Successfully stopped task: b1"}]},"toolUseResult":{"task_id":"b1","task_type":"local_bash","message":"Successfully stopped task: b1"}}""");
        await Assert.That(shell.Subagents).IsEmpty();
    }

    [Test]
    public async Task A_launch_acknowledgement_spelled_agent_id_still_detaches() {
        var launch = R(LaunchResult.Replace("\"agentId\":\"a9f262478e032f427\"", "\"agent_id\":\"a9f262478e032f427\""));
        var detached = (SubagentSignal.Detached)launch.Subagents.Single();
        await Assert.That(detached.CallId).IsEqualTo("toolu_A");
        await Assert.That(detached.AgentId).IsEqualTo("a9f262478e032f427");
    }

    [Test]
    public async Task A_notification_cut_off_before_its_closing_tag_still_finishes() {
        var line = """{"type":"user","message":{"content":"<task-notification>\n<task-id>a9f262478e032f427</task-id>\n<tool-use-id>toolu_A"}}""";
        var finished = (SubagentSignal.Finished)R(line).Subagents.Single();
        await Assert.That(finished.CallId).IsEqualTo("toolu_A");
        await Assert.That(finished.AgentId).IsEqualTo("a9f262478e032f427");
    }

    [Test]
    public async Task A_notification_status_is_matched_without_regard_to_case() {
        var finished = (SubagentSignal.Finished)R(Notification(originKind: true, status: "Completed")).Subagents.Single();
        await Assert.That(finished.Outcome).IsEqualTo(SubagentOutcome.Done);
    }

    [Test]
    public async Task A_stop_message_is_matched_after_leading_whitespace_without_regard_to_case() {
        var stop = R("""{"type":"user","timestamp":"2026-09-17T10:07:00Z","message":{"content":[{"type":"tool_result","tool_use_id":"toolu_S","content":"successfully stopped task: a9f262478e032f427"}]},"toolUseResult":{"task_id":"a9f262478e032f427","task_type":"local_agent","message":"  successfully stopped task: a9f262478e032f427"}}""");
        var stopped = (SubagentSignal.Finished)stop.Subagents.Single();
        await Assert.That(stopped.CallId).IsNull();
        await Assert.That(stopped.AgentId).IsEqualTo("a9f262478e032f427");
        await Assert.That(stopped.Outcome).IsEqualTo(SubagentOutcome.Stopped);
    }
}
