using System.Text.Json.Nodes;

namespace Capacitor.Cli.Continuation;

static class DeclaredPlanState {
    /// <summary>The server's <c>progress.finished</c> when sent; otherwise derived, with
    /// total_known part of it because a plan with no declared task list also reads 0 of 0.</summary>
    public static bool IsFinished(JsonNode? plan) {
        var progress = plan?["progress"];

        if (progress?["finished"] is JsonValue sent && sent.TryGetValue(out bool fromServer)) return fromServer;

        return IsTrue(progress?["total_known"])
            && IntOrZero(progress?["completed"]) == IntOrZero(progress?["total"])
            && IsTrue(plan?["is_complete"]);
    }

    static int  IntOrZero(JsonNode? node) => node is JsonValue v && v.TryGetValue(out int i) ? i : 0;
    static bool IsTrue(JsonNode? node)    => node is JsonValue v && v.TryGetValue(out bool b) && b;
}
