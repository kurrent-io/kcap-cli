using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// What <c>kcap recap --full</c> prints: every closed turn's captured tool calls and results, not only
/// the prompts, replies and file writes the <c>/recap</c> rows carry.
/// </summary>
public class RecapTranscriptTests {
    static readonly DateTimeOffset T0 = new(2026, 10, 29, 19, 0, 0, TimeSpan.Zero);

    static RecapEntry Row(string type, string content, DateTimeOffset at, string? path = null, string? agentId = null, string? agentType = null) =>
        new(type, "s-1", agentId, agentType, content, path, at);

    static string Trace(string traceArrayJson) {
        using var doc = JsonDocument.Parse(traceArrayJson);

        return RecapTranscript.RenderTrace(doc.RootElement);
    }

    static Func<int, Task<TurnDetailResult>> Turns(Dictionary<int, string> traces) =>
        index => Task.FromResult<TurnDetailResult>(
            traces.TryGetValue(index, out var trace) ? new TurnDetailResult.Found($$"""{"trace":{{trace}}}""") : new TurnDetailResult.NotFound());

    /// <summary>The Pi session that showed the edit and dropped the read and the shell call.</summary>
    [Test]
    public async Task A_pi_turn_shows_its_read_its_shell_call_and_its_edit() {
        var text = Trace("""
            [
              {"kind":"user_message","text":"fix the build"},
              {"kind":"tool_invocation","tool":"read","arguments":{"path":"src/a.ts"}},
              {"kind":"tool_result","tool":"read","output":"export const a = 1;"},
              {"kind":"tool_invocation","tool":"bash","arguments":{"command":"npm test"}},
              {"kind":"tool_result","tool":"bash","output":"1 failing"},
              {"kind":"tool_invocation","tool":"edit","arguments":{"path":"src/a.ts","edits":[{"oldText":"1","newText":"2"}]}},
              {"kind":"tool_result","tool":"edit","output":"ok"},
              {"kind":"assistant_message","text":"Fixed."}
            ]
            """);

        await Assert.That(text).Contains("## Tool: read");
        await Assert.That(text).Contains("src/a.ts");
        await Assert.That(text).Contains("export const a = 1;");
        await Assert.That(text).Contains("## Tool: bash");
        await Assert.That(text).Contains("npm test");
        await Assert.That(text).Contains("1 failing");
        await Assert.That(text).Contains("## Tool: edit");
        await Assert.That(text).Contains("## Assistant");
    }

    [Test]
    public async Task A_claude_turn_shows_a_failed_call_as_an_error() {
        var text = Trace("""
            [
              {"kind":"tool_invocation","tool":"Bash","arguments":{"command":"dotnet build"}},
              {"kind":"tool_result","tool":"Bash","output":"error CS1002","is_error":true},
              {"kind":"tool_invocation","tool":"Read","arguments":{"file_path":"/w/Program.cs"}},
              {"kind":"tool_result","tool":"Read","output":"class P {}"}
            ]
            """);

        await Assert.That(text).Contains("## Tool: Bash");
        await Assert.That(text).Contains("### Error" + Environment.NewLine + "error CS1002");
        await Assert.That(text).Contains("## Tool: Read");
        await Assert.That(text).Contains("### Result" + Environment.NewLine + "class P {}");
    }

    [Test]
    public async Task Truncated_and_missing_output_is_said_not_shown_as_complete() {
        var text = Trace("""
            [
              {"kind":"tool_result","tool":"Read","output":"[truncated]","truncated":true,"output_bytes":204800},
              {"kind":"tool_result","tool":"Bash","output":""}
            ]
            """);

        await Assert.That(text).Contains("(The output was truncated; 204800 bytes were recorded.)");
        await Assert.That(text).Contains("(no output recorded)");
    }

    /// <summary>A redaction placeholder is the recorded content, so it is printed as recorded.</summary>
    [Test]
    public async Task A_redacted_value_stays_visible_as_redacted() {
        var text = Trace("""[ {"kind":"tool_result","tool":"Bash","output":"token=[REDACTED:github_pat]"} ]""");

        await Assert.That(text).Contains("token=[REDACTED:github_pat]");
    }

    [Test]
    public async Task Subagent_calls_are_grouped_under_their_agent_and_the_main_session_is_marked_on_return() {
        var text = Trace("""
            [
              {"kind":"tool_invocation","tool":"Task","arguments":{"subagent_type":"Explore"}},
              {"kind":"tool_invocation","tool":"Grep","arguments":{"pattern":"Foo"},"agent_id":"a-1","agent_type":"Explore"},
              {"kind":"tool_result","tool":"Grep","output":"src/Foo.cs","agent_id":"a-1","agent_type":"Explore"},
              {"kind":"assistant_message","text":"Found it."}
            ]
            """);

        var subagent = text.IndexOf("### Subagent: Explore (a-1)", StringComparison.Ordinal);
        var grep     = text.IndexOf("## Tool: Grep", StringComparison.Ordinal);
        var main     = text.IndexOf("### (main session)", StringComparison.Ordinal);

        await Assert.That(subagent).IsGreaterThanOrEqualTo(0);
        await Assert.That(grep).IsGreaterThan(subagent);
        await Assert.That(main).IsGreaterThan(grep);
    }

    [Test]
    public async Task Full_prints_plans_then_every_turn_with_its_tools() {
        const string turns = """[ {"turn_index":1,"last_event_at":"2026-10-29T19:10:00Z"}, {"turn_index":0,"last_event_at":"2026-10-29T19:05:00Z"} ]""";

        var text = await RecapTranscript.RenderSessionAsync(
            [Row("plan", "1. read 2. fix", T0), Row("user_prompt", "fix it", T0.AddMinutes(1))],
            turns,
            Turns(new() {
                [0] = """[ {"kind":"tool_invocation","tool":"read","arguments":{"path":"a.ts"}} ]""",
                [1] = """[ {"kind":"tool_invocation","tool":"bash","arguments":{"command":"npm test"}} ]""",
            }));

        var plan  = text.IndexOf("## Plan", StringComparison.Ordinal);
        var turn0 = text.IndexOf("# Turn 0", StringComparison.Ordinal);
        var turn1 = text.IndexOf("# Turn 1", StringComparison.Ordinal);

        await Assert.That(plan).IsEqualTo(0);
        await Assert.That(turn0).IsGreaterThan(plan);
        await Assert.That(turn1).IsGreaterThan(turn0);
        await Assert.That(text).Contains("## Tool: read");
        await Assert.That(text).Contains("## Tool: bash");
        await Assert.That(text).DoesNotContain("## User Prompt");
    }

    [Test]
    public async Task A_turn_that_cannot_be_read_is_named_and_the_rest_still_print() {
        const string turns = """[ {"turn_index":0}, {"turn_index":1}, {"turn_index":2} ]""";

        var text = await RecapTranscript.RenderSessionAsync([], turns, index => index switch {
            0 => throw new CapacitorApiException(500, "Server returned HTTP 500"),
            1 => Task.FromResult<TurnDetailResult>(new TurnDetailResult.NotFound()),
            _ => Task.FromResult<TurnDetailResult>(new TurnDetailResult.Found("""{"trace":[{"kind":"assistant_message","text":"done"}]}""")),
        });

        await Assert.That(text).Contains("(Turn 0 could not be read: Server returned HTTP 500)");
        await Assert.That(text).Contains("(Turn 1 was not found on the server.)");
        await Assert.That(text).Contains("done");
    }

    /// <summary>A session with no closed turn still shows what it has, and says what is missing.</summary>
    [Test]
    public async Task Without_closed_turns_the_rows_print_under_a_note_that_tools_are_missing() {
        var text = await RecapTranscript.RenderSessionAsync(
            [Row("user_prompt", "start", T0), Row("edit", "old: a\nnew: b", T0.AddMinutes(1), "src/a.ts")],
            "[]",
            _ => throw new InvalidOperationException("no turn may be fetched"));

        await Assert.That(text).Contains("No closed turn is recorded yet");
        await Assert.That(text).Contains("tool calls and their results appear once the turn closes");
        await Assert.That(text).Contains("## User Prompt");
        await Assert.That(text).Contains("## Edit src/a.ts");
    }

    [Test]
    public async Task Activity_after_the_last_closed_turn_is_kept() {
        const string turns = """[ {"turn_index":0,"last_event_at":"2026-10-29T19:05:00Z"} ]""";

        var text = await RecapTranscript.RenderSessionAsync(
            [Row("user_prompt", "earlier", T0), Row("user_prompt", "still running", T0.AddMinutes(20))],
            turns,
            Turns(new() { [0] = """[ {"kind":"user_message","text":"earlier"} ]""" }));

        await Assert.That(text).Contains("# After turn 0");
        await Assert.That(text).Contains("still running");
        await Assert.That(text.IndexOf("still running", StringComparison.Ordinal))
            .IsGreaterThan(text.IndexOf("# After turn 0", StringComparison.Ordinal));
        await Assert.That(text).DoesNotContain("## User Prompt" + Environment.NewLine + "earlier");
    }

    [Test]
    public async Task Subagent_rows_keep_their_grouping_in_the_fallback() {
        var text = RecapTranscript.RenderEntries([
            Row("user_prompt", "go", T0),
            Row("assistant_text", "exploring", T0.AddMinutes(1), agentId: "a-1", agentType: "Explore"),
            Row("assistant_text", "back", T0.AddMinutes(2)),
        ]);

        await Assert.That(text).Contains("### Agent (Explore)" + Environment.NewLine + Environment.NewLine + "## Assistant" + Environment.NewLine + "exploring");
    }

    [Test]
    public async Task An_unreadable_turn_list_reads_as_no_turns() {
        await Assert.That(RecapTranscript.ParseTurns("<html>")).IsEmpty();
        await Assert.That(RecapTranscript.ParseTurns(null)).IsEmpty();
        await Assert.That(string.Join(",", RecapTranscript.ParseTurns("""[ {"turn_index":"x"}, {"turn_index":3} ]""").Select(t => t.Index)))
            .IsEqualTo("3");
    }
}
