using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.Cli.Harness.Claude;

/// <summary>Whether a Stop hook blocks the stop once, asking the agent to declare its loose ends and
/// present next work. Every gate must agree: the server asked for it in the ack, the profile has not
/// opted out of next-work, the agent is not already continuing from a Stop block, and its last message
/// reads as a wrap-up.</summary>
static class ClaudeCompletionNudge {
    internal const int LastMessageCap = 4000;

    internal const string BlockDecision =
        """{"decision":"block","reason":"kcap: If the user's task is now complete — (1) declare any remaining loose ends with declare_loose_end, one call per item; (2) call get_next_work and tell the user, in a few lines, what to consider working on next and why. If the task is not complete, say so briefly and stop again."}""";

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

    internal static bool ShouldBlock(string? ackBody, bool nextWorkDisabled, JsonNode? payload) {
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
