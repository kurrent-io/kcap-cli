namespace Capacitor.Cli.Core.Tests.Unit.Harness.Claude;

using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Policy;

public class ClaudeJudgeDeclarationReaderTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string Rejection =
        "The user doesn't want to proceed with this tool use. The tool use was rejected (eg. if it was a file edit, the new_string was NOT written to the file). STOP what you are doing and wait for the user to tell you how to proceed.";

    static string Id(int n) => $"00000000-0000-0000-0000-{n:D12}";

    static string Human(int n, string text, string? promptId = null, bool meta = false, bool sidechain = false) {
        var line = new JsonObject {
            ["type"] = "user", ["uuid"] = Id(n), ["promptId"] = promptId ?? $"p{n}",
            ["message"] = new JsonObject { ["role"] = "user", ["content"] = text },
        };
        if (meta) line["isMeta"] = true;
        if (sidechain) line["isSidechain"] = true;
        return line.ToJsonString();
    }

    static string ToolUse(string callId, string tool, JsonObject input, string? ts = null) => new JsonObject {
        ["type"] = "assistant", ["uuid"] = Guid.NewGuid().ToString(), ["timestamp"] = ts,
        ["message"] = new JsonObject {
            ["role"] = "assistant",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = callId, ["name"] = tool, ["input"] = input }),
        },
    }.ToJsonString();

    static string Result(string callId, string text, bool isError, string promptId = "p1", string? ts = null) => new JsonObject {
        ["type"] = "user", ["uuid"] = Guid.NewGuid().ToString(), ["promptId"] = promptId, ["timestamp"] = ts,
        ["message"] = new JsonObject {
            ["role"] = "user",
            ["content"] = new JsonArray(new JsonObject {
                ["type"] = "tool_result", ["tool_use_id"] = callId, ["is_error"] = isError, ["content"] = text,
            }),
        },
    }.ToJsonString();

    string Write(params string[] lines) => Tmp.CreateFile("session.jsonl", string.Join("\n", lines) + "\n");

    static JsonObject Cmd(string command) => new() { ["command"] = command };

    [Test]
    public async Task Declares_the_last_sixteen_human_messages_oldest_first() {
        var lines = Enumerable.Range(1, 20).Select(n => Human(n, $"message {n}")).ToArray();
        var d = ClaudeJudgeDeclarationReader.Read(Write(lines), "toolu_9", null);

        var ids = d.Turns!.UserMessages.Select(m => m.Id).ToArray();
        await Assert.That(ids.Length).IsEqualTo(16);
        await Assert.That(ids[0]).IsEqualTo(Id(5));
        await Assert.That(ids[^1]).IsEqualTo(Id(20));
        await Assert.That(d.Turns.UserMessages[^1].PromptId).IsEqualTo("p20");
        await Assert.That(d.Turns.ToolUseId).IsEqualTo("toolu_9");
    }

    /// <summary>The server proves the turn set against its own recording with the same exclusions,
    /// so a meta line, a sidechain line, a tool result or a harness payload declared here would fail
    /// that proof and cost the window.</summary>
    [Test]
    public async Task Excludes_meta_sidechain_results_and_harness_payloads() {
        var d = ClaudeJudgeDeclarationReader.Read(Write(
            Human(1, "please refactor"),
            Human(2, "<command-name>/clear</command-name>", meta: true),
            Human(3, "a subagent prompt", sidechain: true),
            Result("toolu_1", "ok", isError: false),
            Human(4, "<task-notification><task-id>t</task-id><status>completed</status></task-notification>"),
            Human(5, "<local-command-stdout>done</local-command-stdout>")), null, null);

        await Assert.That(d.Turns!.UserMessages.Select(m => m.Id).ToArray()).IsEquivalentTo(new[] { Id(1) });
    }

    [Test]
    public async Task A_prompt_queued_mid_turn_is_a_human_message() {
        var queued = new JsonObject {
            ["type"] = "attachment", ["uuid"] = Id(2), ["promptId"] = "p2",
            ["attachment"] = new JsonObject { ["type"] = "queued_command", ["commandMode"] = "prompt", ["prompt"] = "also run the tests" },
        }.ToJsonString();
        var d = ClaudeJudgeDeclarationReader.Read(Write(Human(1, "fix it"), queued), null, null);

        await Assert.That(d.Turns!.UserMessages[^1].Id).IsEqualTo(Id(2));
    }

    [Test]
    public async Task A_human_rejection_is_declared_with_its_tool_and_target() {
        var d = ClaudeJudgeDeclarationReader.Read(Write(
            Human(1, "ship it"),
            ToolUse("toolu_1", "Bash", Cmd("git push --force")),
            Result("toolu_1", Rejection, isError: true, promptId: "p1")), null, null);

        await Assert.That(d.Refusals.Complete).IsTrue();
        var refusal = d.Refusals.Entries.Single();
        await Assert.That(refusal.ToolUseId).IsEqualTo("toolu_1");
        await Assert.That(refusal.Tool).IsEqualTo("Bash");
        await Assert.That(refusal.Target).IsEqualTo("git push --force");
        await Assert.That(refusal.PromptId).IsEqualTo("p1");
    }

    /// <summary>Only a human's no is a refusal: a hook's or the classifier's denial, an interrupt and
    /// an ordinary failed command are not.</summary>
    [Test]
    public async Task Other_errors_are_not_refusals() {
        var d = ClaudeJudgeDeclarationReader.Read(Write(
            Human(1, "go"),
            ToolUse("toolu_1", "Bash", Cmd("a")), Result("toolu_1", "Permission denied by hook: no", isError: true),
            ToolUse("toolu_2", "Bash", Cmd("b")), Result("toolu_2", "[Request interrupted by user for tool use]", isError: true),
            ToolUse("toolu_3", "Bash", Cmd("c")), Result("toolu_3", "exit code 1", isError: true),
            ToolUse("toolu_4", "Bash", Cmd("d")), Result("toolu_4", Rejection, isError: false)), null, null);

        await Assert.That(d.Refusals.Complete).IsTrue();
        await Assert.That(d.Refusals.Entries).IsEmpty();
    }

    [Test]
    public async Task Refusals_are_newest_first_and_more_than_thirty_two_are_incomplete() {
        var lines = new List<string> { Human(1, "go") };
        for (var i = 0; i < 34; i++) {
            var ts = $"2026-10-05T10:{i:D2}:00.000Z";
            lines.Add(ToolUse($"toolu_{i}", "Bash", Cmd($"cmd {i}"), ts));
            lines.Add(Result($"toolu_{i}", Rejection, isError: true, ts: ts));
        }
        var d = ClaudeJudgeDeclarationReader.Read(Write([.. lines]), null, null);

        await Assert.That(d.Refusals.Complete).IsFalse();
        await Assert.That(d.Refusals.Entries.Length).IsEqualTo(32);
        await Assert.That(d.Refusals.Entries[0].ToolUseId).IsEqualTo("toolu_33");
    }

    [Test]
    public async Task A_refusal_whose_call_is_missing_makes_the_list_incomplete() {
        var d = ClaudeJudgeDeclarationReader.Read(Write(
            Human(1, "go"), Result("toolu_1", Rejection, isError: true)), null, null);

        await Assert.That(d.Refusals.Complete).IsFalse();
        await Assert.That(d.Refusals.Entries.Single().ToolUseId).IsEqualTo("toolu_1");
    }

    [Test]
    public async Task A_target_over_the_wire_limit_is_clipped_and_the_list_incomplete() {
        var d = ClaudeJudgeDeclarationReader.Read(Write(
            Human(1, "go"),
            ToolUse("toolu_1", "Bash", Cmd(new string('x', 2000))),
            Result("toolu_1", Rejection, isError: true)), null, null);

        await Assert.That(d.Refusals.Complete).IsFalse();
        await Assert.That(d.Refusals.Entries.Single().Target.Length).IsEqualTo(1024);
    }

    [Test]
    public async Task A_subagents_refusal_counts_for_the_session() {
        var main = Write(Human(1, "go"));
        Tmp.CreateFile(["session", "subagents", "agent-a1.jsonl"],
            ToolUse("toolu_s", "Write", new JsonObject { ["file_path"] = "/repo/x.cs", ["content"] = "" }) + "\n"
          + Result("toolu_s", Rejection, isError: true) + "\n");

        var d = ClaudeJudgeDeclarationReader.Read(main, null, "/repo");

        await Assert.That(d.Refusals.Complete).IsTrue();
        await Assert.That(d.Refusals.Entries.Single().Target).IsEqualTo("/repo/x.cs");
    }

    [Test]
    public async Task An_unreadable_transcript_declares_no_turns_and_unknown_refusals() {
        var d = ClaudeJudgeDeclarationReader.Read(Tmp.PathTo("missing.jsonl"), "toolu_1", null);

        await Assert.That(d.Turns).IsNull();
        await Assert.That(d.Refusals.Complete).IsFalse();
        await Assert.That(d.Refusals.Source).IsEqualTo(PolicyJudgeRefusalsV1.SourceTranscript);
    }

    [Test]
    public async Task A_line_still_being_written_is_skipped() {
        var path = Tmp.CreateFile("session.jsonl", Human(1, "first") + "\n" + Human(2, "second")[..20]);

        var d = ClaudeJudgeDeclarationReader.Read(path, null, null);

        await Assert.That(d.Turns!.UserMessages.Select(m => m.Id).ToArray()).IsEquivalentTo(new[] { Id(1) });
        await Assert.That(d.Refusals.Complete).IsTrue();
    }

    string State => Tmp.PathTo("state.json");

    static void Append(string path, params string[] lines) => File.AppendAllText(path, string.Join("\n", lines) + "\n");

    [Test]
    public async Task A_resumed_read_takes_only_what_was_appended_and_keeps_the_window() {
        var path = Write(Human(1, "first"));
        ClaudeJudgeDeclarationReader.Read(path, null, null, State);
        Append(path, Human(2, "second"));

        var d = ClaudeJudgeDeclarationReader.Read(path, null, null, State);

        await Assert.That(d.Turns!.UserMessages.Select(m => m.Id).ToArray()).IsEquivalentTo(new[] { Id(1), Id(2) });
    }

    /// <summary>The prompt that a refusal answers is raised between the call and its result, so the
    /// call's line has usually been consumed by an earlier hook's read.</summary>
    [Test]
    public async Task A_refusal_resolves_a_call_an_earlier_read_consumed() {
        var path = Write(Human(1, "go"), ToolUse("toolu_1", "Bash", Cmd("git push --force")));
        ClaudeJudgeDeclarationReader.Read(path, null, null, State);
        Append(path, Result("toolu_1", Rejection, isError: true));

        var d = ClaudeJudgeDeclarationReader.Read(path, null, null, State);

        await Assert.That(d.Refusals.Complete).IsTrue();
        await Assert.That(d.Refusals.Entries.Single().Target).IsEqualTo("git push --force");
    }

    [Test]
    public async Task Refusals_found_by_earlier_reads_stay_declared() {
        var path = Write(Human(1, "go"), ToolUse("toolu_1", "Bash", Cmd("rm -rf build")), Result("toolu_1", Rejection, isError: true));
        ClaudeJudgeDeclarationReader.Read(path, null, null, State);
        Append(path, Human(2, "carry on"));

        var d = ClaudeJudgeDeclarationReader.Read(path, null, null, State);

        await Assert.That(d.Refusals.Entries.Single().ToolUseId).IsEqualTo("toolu_1");
    }

    [Test]
    public async Task A_line_completed_after_a_read_is_taken_whole_by_the_next() {
        var second = Human(2, "second");
        var path = Tmp.CreateFile("session.jsonl", Human(1, "first") + "\n" + second[..20]);
        ClaudeJudgeDeclarationReader.Read(path, null, null, State);
        File.AppendAllText(path, second[20..] + "\n");

        var d = ClaudeJudgeDeclarationReader.Read(path, null, null, State);

        await Assert.That(d.Turns!.UserMessages.Select(m => m.Id).ToArray()).IsEquivalentTo(new[] { Id(1), Id(2) });
    }

    [Test]
    public async Task A_replaced_shorter_file_is_read_again_from_the_top() {
        var path = Write(Human(1, "first"), Human(2, "second"), ToolUse("toolu_1", "Bash", Cmd("x")), Result("toolu_1", Rejection, isError: true));
        ClaudeJudgeDeclarationReader.Read(path, null, null, State);
        File.WriteAllText(path, Human(3, "new") + "\n");

        var d = ClaudeJudgeDeclarationReader.Read(path, null, null, State);

        await Assert.That(d.Turns!.UserMessages.Select(m => m.Id).ToArray()).IsEquivalentTo(new[] { Id(3) });
        await Assert.That(d.Refusals.Entries).IsEmpty();
    }

    [Test]
    public async Task A_corrupt_state_costs_only_a_full_read() {
        var path = Write(Human(1, "first"));
        Tmp.CreateFile("state.json", "{ not json");

        var d = ClaudeJudgeDeclarationReader.Read(path, null, null, State);

        await Assert.That(d.Turns!.UserMessages.Single().Id).IsEqualTo(Id(1));
    }

    [Test]
    public async Task A_result_whose_rejection_follows_an_empty_text_block_is_a_refusal() {
        var result = new JsonObject {
            ["type"] = "user", ["uuid"] = Guid.NewGuid().ToString(), ["promptId"] = "p1",
            ["message"] = new JsonObject {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject {
                    ["type"] = "tool_result", ["tool_use_id"] = "toolu_1", ["is_error"] = true,
                    ["content"] = new JsonArray(
                        new JsonObject { ["type"] = "text", ["text"] = "" },
                        new JsonObject { ["type"] = "text", ["text"] = Rejection }),
                }),
            },
        }.ToJsonString();
        var d = ClaudeJudgeDeclarationReader.Read(Write(Human(1, "go"), ToolUse("toolu_1", "Bash", Cmd("x")), result), null, null);

        await Assert.That(d.Refusals.Entries.Single().ToolUseId).IsEqualTo("toolu_1");
    }

    /// <summary>A backlog larger than one read's allowance is caught up over several hooks; until it
    /// is, nothing is declared as known.</summary>
    [Test]
    public async Task A_backlog_over_the_allowance_declares_nothing_until_caught_up() {
        var lines = Enumerable.Range(1, 40).Select(n => Human(n, $"message {n}")).ToArray();
        var path = Write(lines);
        var allowance = new FileInfo(path).Length / 3;

        var first = ClaudeJudgeDeclarationReader.Read(path, null, null, State, allowance);
        await Assert.That(first.Turns).IsNull();
        await Assert.That(first.Refusals.Complete).IsFalse();

        ClaudeJudgeDeclarations last = first;
        for (var i = 0; i < 5 && last.Turns is null; i++)
            last = ClaudeJudgeDeclarationReader.Read(path, null, null, State, allowance);

        await Assert.That(last.Turns!.UserMessages[^1].Id).IsEqualTo(Id(40));
        await Assert.That(last.Refusals.Complete).IsTrue();
    }

    [Test]
    public async Task A_line_longer_than_the_allowance_is_skipped_not_stalled_on() {
        var path = Write(Human(1, new string('x', 4000)), Human(2, "after"));

        ClaudeJudgeDeclarationReader.Read(path, null, null, State, 1000);
        var d = ClaudeJudgeDeclarationReader.Read(path, null, null, State, 1000);

        await Assert.That(d.Turns!.UserMessages.Single().Id).IsEqualTo(Id(2));
    }
}
