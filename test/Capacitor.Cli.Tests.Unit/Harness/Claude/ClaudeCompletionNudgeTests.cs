using System.Text.Json.Nodes;
using Capacitor.Cli.Harness.Claude;

namespace Capacitor.Cli.Tests.Unit.Harness.Claude;

public class ClaudeCompletionNudgeTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string Nudge   = """{"completion_nudge":true}""";
    const string WrapUp  = "## Summary\nFixed the parser; all tests pass.";
    const string MidTask = "Not complete yet — waiting on your answer?";

    static JsonObject Payload(string? lastMessage = WrapUp, bool? stopHookActive = false, string? transcriptPath = null) {
        var hook = new JsonObject { ["hook_event_name"] = "Stop", ["session_id"] = "s" };
        if (lastMessage is not null) hook["last_assistant_message"] = lastMessage;
        if (stopHookActive is { } active) hook["stop_hook_active"] = active;
        if (transcriptPath is not null) hook["transcript_path"] = transcriptPath;
        return hook;
    }

    [Test]
    public async Task Blocks_when_the_ack_the_profile_and_the_message_all_agree() =>
        await Assert.That(ClaudeCompletionNudge.ShouldBlock(Nudge, nextWorkDisabled: false, Payload())).IsTrue();

    [Test]
    [Arguments("")]
    [Arguments("{}")]
    [Arguments("""{"completion_nudge":false}""")]
    [Arguments("""{"completion_nudge":"true"}""")]
    [Arguments("not json")]
    [Arguments("[true]")]
    public async Task An_ack_without_a_true_completion_nudge_never_blocks(string ack) =>
        await Assert.That(ClaudeCompletionNudge.ShouldBlock(ack, nextWorkDisabled: false, Payload())).IsFalse();

    [Test]
    public async Task An_absent_ack_body_never_blocks() =>
        await Assert.That(ClaudeCompletionNudge.ShouldBlock(null, nextWorkDisabled: false, Payload())).IsFalse();

    [Test]
    public async Task The_next_work_opt_out_never_blocks() =>
        await Assert.That(ClaudeCompletionNudge.ShouldBlock(Nudge, nextWorkDisabled: true, Payload())).IsFalse();

    [Test]
    public async Task A_message_that_is_not_a_wrap_up_never_blocks() =>
        await Assert.That(ClaudeCompletionNudge.ShouldBlock(Nudge, nextWorkDisabled: false, Payload(MidTask))).IsFalse();

    /// <summary>The agent is already continuing from a Stop block; blocking again would loop.</summary>
    [Test]
    public async Task A_stop_that_is_already_continuing_from_a_block_never_blocks() =>
        await Assert.That(ClaudeCompletionNudge.ShouldBlock(Nudge, nextWorkDisabled: false, Payload(stopHookActive: true))).IsFalse();

    [Test]
    public async Task Without_an_inline_message_the_transcript_tail_decides() {
        var wrapUp  = Tmp.CreateFile("done.jsonl", Line(WrapUp));
        var midTask = Tmp.CreateFile("mid.jsonl", Line(MidTask));

        await Assert.That(ClaudeCompletionNudge.ShouldBlock(Nudge, false, Payload(lastMessage: null, transcriptPath: wrapUp))).IsTrue();
        await Assert.That(ClaudeCompletionNudge.ShouldBlock(Nudge, false, Payload(lastMessage: null, transcriptPath: midTask))).IsFalse();
        await Assert.That(ClaudeCompletionNudge.ShouldBlock(Nudge, false, Payload(lastMessage: null, transcriptPath: Tmp.PathTo("absent")))).IsFalse();
    }

    [Test]
    public async Task The_block_decision_is_one_json_object_naming_both_tools() {
        var decision = JsonNode.Parse(ClaudeCompletionNudge.BlockDecision)!;

        await Assert.That(ClaudeCompletionNudge.BlockDecision).DoesNotContain("\n");
        await Assert.That(decision["decision"]!.GetValue<string>()).IsEqualTo("block");
        await Assert.That(decision["reason"]!.GetValue<string>()).StartsWith("kcap: If the user's task is now complete — (1) declare any remaining loose ends with declare_loose_end");
        await Assert.That(decision["reason"]!.GetValue<string>()).Contains("(2) call get_next_work");
    }

    static string Line(string text) =>
        """{"type":"assistant","message":{"content":[{"type":"text","text":""" + System.Text.Json.JsonSerializer.Serialize(text) + "}]}}\n";
}
