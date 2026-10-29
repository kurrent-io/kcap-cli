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
        await Assert.That(ClaudeCompletionNudge.ShouldBlock(Nudge, nextWorkDisabled: false, Payload(), SessionStartAudience.Interactive)).IsTrue();

    [Test]
    [Arguments("")]
    [Arguments("{}")]
    [Arguments("""{"completion_nudge":false}""")]
    [Arguments("""{"completion_nudge":"true"}""")]
    [Arguments("not json")]
    [Arguments("[true]")]
    public async Task An_ack_without_a_true_completion_nudge_never_blocks(string ack) =>
        await Assert.That(ClaudeCompletionNudge.ShouldBlock(ack, nextWorkDisabled: false, Payload(), SessionStartAudience.Interactive)).IsFalse();

    [Test]
    public async Task An_absent_ack_body_never_blocks() =>
        await Assert.That(ClaudeCompletionNudge.ShouldBlock(null, nextWorkDisabled: false, Payload(), SessionStartAudience.Interactive)).IsFalse();

    [Test]
    public async Task The_next_work_opt_out_never_blocks() =>
        await Assert.That(ClaudeCompletionNudge.ShouldBlock(Nudge, nextWorkDisabled: true, Payload(), SessionStartAudience.Interactive)).IsFalse();

    [Test]
    public async Task A_message_that_is_not_a_wrap_up_never_blocks() =>
        await Assert.That(ClaudeCompletionNudge.ShouldBlock(Nudge, nextWorkDisabled: false, Payload(MidTask), SessionStartAudience.Interactive)).IsFalse();

    /// <summary>The agent is already continuing from a Stop block; blocking again would loop.</summary>
    [Test]
    public async Task A_stop_that_is_already_continuing_from_a_block_never_blocks() =>
        await Assert.That(ClaudeCompletionNudge.ShouldBlock(Nudge, nextWorkDisabled: false, Payload(stopHookActive: true), SessionStartAudience.Interactive)).IsFalse();

    /// <summary>Every other gate agrees, so only the participant marker can withhold the block.</summary>
    [Test]
    public async Task A_flow_participant_is_never_blocked() =>
        await Assert.That(ClaudeCompletionNudge.ShouldBlock(Nudge, nextWorkDisabled: false, Payload(),
            new SessionStartAudience(IsHosted: true, IsFlowParticipant: true))).IsFalse();

    [Test]
    public async Task A_hosted_agent_that_is_not_a_participant_is_still_blocked() =>
        await Assert.That(ClaudeCompletionNudge.ShouldBlock(Nudge, nextWorkDisabled: false, Payload(),
            new SessionStartAudience(IsHosted: true, IsFlowParticipant: false))).IsTrue();

    [Test]
    public async Task Without_an_inline_message_the_transcript_tail_decides() {
        var wrapUp  = Tmp.CreateFile("done.jsonl", Line(WrapUp));
        var midTask = Tmp.CreateFile("mid.jsonl", Line(MidTask));

        await Assert.That(ClaudeCompletionNudge.ShouldBlock(Nudge, false, Payload(lastMessage: null, transcriptPath: wrapUp), SessionStartAudience.Interactive)).IsTrue();
        await Assert.That(ClaudeCompletionNudge.ShouldBlock(Nudge, false, Payload(lastMessage: null, transcriptPath: midTask), SessionStartAudience.Interactive)).IsFalse();
        await Assert.That(ClaudeCompletionNudge.ShouldBlock(Nudge, false, Payload(lastMessage: null, transcriptPath: Tmp.PathTo("absent")), SessionStartAudience.Interactive)).IsFalse();
    }

    [Test]
    public async Task The_block_decision_is_one_json_object_naming_the_close_declare_and_next_work_steps() {
        var decision = JsonNode.Parse(ClaudeCompletionNudge.BlockDecision)!;

        await Assert.That(ClaudeCompletionNudge.BlockDecision).DoesNotContain("\n");
        await Assert.That(decision["decision"]!.GetValue<string>()).IsEqualTo("block");
        await Assert.That(decision["reason"]!.GetValue<string>()).StartsWith("kcap: If the piece of work the user framed has reached a terminal state");
        await Assert.That(decision["reason"]!.GetValue<string>()).Contains("(1) close_loose_end each loose end whose work this session finished");
        // Declared and claimed ends are found two different ways: a declared one by its declaration_id or among the
        // ends the session sighted, a claimed one by the claim.
        await Assert.That(decision["reason"]!.GetValue<string>()).Contains("the ones it declared (pass the declaration_id, or find them with list_loose_ends by session_id)");
        await Assert.That(decision["reason"]!.GetValue<string>()).Contains("the ones it claimed (list_loose_ends by claimed_session_id; pass each claim_id)");
        // session_id lists the ends a session sighted, which is no claim on them.
        await Assert.That(decision["reason"]!.GetValue<string>()).DoesNotContain("session_id or");
        await Assert.That(decision["reason"]!.GetValue<string>()).Contains("(2) declare any remaining loose ends with declare_loose_end");
        await Assert.That(decision["reason"]!.GetValue<string>()).Contains("unless it says this session's attached work is still open");
        await Assert.That(decision["reason"]!.GetValue<string>()).Contains("(3) call get_next_work");

        // Closing what is finished comes before declaring what is left, and both before next work.
        var reason = decision["reason"]!.GetValue<string>();
        await Assert.That(reason.IndexOf("(1) close_loose_end", StringComparison.Ordinal))
            .IsLessThan(reason.IndexOf("(2) declare", StringComparison.Ordinal));
        await Assert.That(reason.IndexOf("(2) declare", StringComparison.Ordinal))
            .IsLessThan(reason.IndexOf("(3) call get_next_work", StringComparison.Ordinal));
    }

    static string Line(string text) =>
        """{"type":"assistant","message":{"content":[{"type":"text","text":""" + System.Text.Json.JsonSerializer.Serialize(text) + "}]}}\n";
}
