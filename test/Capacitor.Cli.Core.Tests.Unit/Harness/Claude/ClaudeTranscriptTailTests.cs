using System.Text.Json;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Claude;

public class ClaudeTranscriptTailTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static string Assistant(params string[] texts) =>
        """{"type":"assistant","message":{"role":"assistant","content":[""" +
        string.Join(",", texts.Select(t => """{"type":"text","text":""" + JsonSerializer.Serialize(t) + "}")) +
        "]}}";

    const string ToolUse = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{}}]}}""";
    const string User    = """{"type":"user","message":{"role":"user","content":"go on"}}""";
    const string StopSummary = """{"type":"system","subtype":"stop_hook_summary"}""";

    [Test]
    public async Task Returns_every_text_block_of_the_most_recent_assistant_line() {
        var path = Tmp.CreateFile("t.jsonl", string.Join("\n", Assistant("first"), User, Assistant("## Summary", "All done."), ToolUse, StopSummary) + "\n");

        await Assert.That(ClaudeTranscriptTail.LastAssistantText(path, 4000)).IsEqualTo("## Summary\n\nAll done.");
    }

    [Test]
    public async Task Reads_string_content() {
        var path = Tmp.CreateFile("t.jsonl", """{"type":"assistant","message":{"role":"assistant","content":"Done. All tests pass."}}""" + "\n");

        await Assert.That(ClaudeTranscriptTail.LastAssistantText(path, 4000)).IsEqualTo("Done. All tests pass.");
    }

    [Test]
    public async Task Skips_a_subagents_record() {
        const string Sidechain = """{"type":"assistant","isSidechain":true,"message":{"role":"assistant","content":[{"type":"text","text":"Subagent: done."}]}}""";
        var path = Tmp.CreateFile("t.jsonl", Assistant("Main agent still working") + "\n" + Sidechain + "\n");

        await Assert.That(ClaudeTranscriptTail.LastAssistantText(path, 4000)).IsEqualTo("Main agent still working");
    }

    [Test]
    public async Task Keeps_only_the_end_of_a_message_past_the_cap() {
        var path = Tmp.CreateFile("t.jsonl", Assistant(new string('a', 50) + "tail?") + "\n");

        await Assert.That(ClaudeTranscriptTail.LastAssistantText(path, 10)).IsEqualTo("aaaaatail?");
    }

    [Test]
    public async Task Reads_only_the_tail_of_a_file_larger_than_the_window() {
        var filler = Assistant(new string('x', ClaudeTranscriptTail.TailBytes));
        var path   = Tmp.CreateFile("t.jsonl", Assistant("too far back") + "\n" + filler + "\n" + User + "\n");

        await Assert.That(ClaudeTranscriptTail.LastAssistantText(path, 4000)).IsNull();
    }

    [Test]
    public async Task A_missing_file_or_no_assistant_text_yields_null() {
        await Assert.That(ClaudeTranscriptTail.LastAssistantText(Tmp.PathTo("absent.jsonl"), 4000)).IsNull();
        await Assert.That(ClaudeTranscriptTail.LastAssistantText(null, 4000)).IsNull();

        var path = Tmp.CreateFile("t.jsonl", User + "\nnot json\n" + ToolUse + "\n");
        await Assert.That(ClaudeTranscriptTail.LastAssistantText(path, 4000)).IsNull();
    }
}
