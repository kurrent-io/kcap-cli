using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.Cli.Harness.Claude;

/// <summary>Whether a Stop hook blocks the stop once, asking the agent to close the loose ends it finished,
/// declare the rest and present next work. Every gate must agree: the server asked for it in the ack, the profile has not
/// opted out of next-work, the agent is not already continuing from a Stop block, and its last message
/// reads as a wrap-up. A flow participant is never blocked, whatever the ack says.</summary>
static class ClaudeCompletionNudge {
    internal const int LastMessageCap = 4000;

    internal const string BlockDecision =
        """{"decision":"block","reason":"kcap: If the piece of work the user framed has reached a terminal state — a PR merged, an issue closed, a question answered with nothing pending, or the user done with it — (1) close_loose_end each loose end whose work this session finished, the ones it declared (pass the declaration_id, or find them with list_loose_ends by session_id) and the ones it claimed (list_loose_ends by claimed_session_id; pass each claim_id); (2) declare any remaining loose ends with declare_loose_end, one call per item; (3) call get_next_work and, unless it says this session's attached work is still open and the user has neither asked for next work nor said they are done with or parking it, tell the user, in a few lines, what to consider working on next and why. Otherwise say nothing about next work and stop again."}""";

    /// <summary>A server that predates the field answers with an empty body, and a current one omits
    /// the field rather than sending false; neither nudges.</summary>
    internal static bool AckRequestsNudge(string? ackBody) {
        if (string.IsNullOrWhiteSpace(ackBody)) return false;
        try {
            return JsonNode.Parse(ackBody) is JsonObject ack
                && ack["completion_nudge"] is JsonValue value
                && value.TryGetValue<bool>(out var nudge)
                && nudge;
        } catch {
            return false;
        }
    }

    internal static bool ShouldBlock(string? ackBody, bool nextWorkDisabled, JsonNode? payload, SessionStartAudience audience) {
        if (!audience.GetsDriverGuidance) return false;
        if (nextWorkDisabled || !AckRequestsNudge(ackBody) || payload is not JsonObject hook) return false;
        if (ReadBool(hook, "stop_hook_active")) return false;

        return WrapUpSignals.LooksLikeWrapUp(LastAssistantText(hook));
    }

    /// <summary>The payload's own <c>last_assistant_message</c> when the Claude build sends one,
    /// otherwise the transcript's tail.</summary>
    internal static string? LastAssistantText(JsonObject hook) {
        if (ReadString(hook, "last_assistant_message") is { Length: > 0 } inline)
            return inline.Length <= LastMessageCap ? inline : inline[^LastMessageCap..];

        return ClaudeTranscriptTail.LastAssistantText(ReadString(hook, "transcript_path"), LastMessageCap);
    }

    static string? ReadString(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    static bool ReadBool(JsonObject obj, string key) =>
        obj[key] is JsonValue value && value.TryGetValue<bool>(out var b) && b;
}
