using System.Text.Json.Nodes;

namespace Capacitor.Cli;

/// <summary>
/// Builds the "recurring lessons from prior sessions" text fragment from a
/// server <c>/hooks/session-start</c> response body. Returns plain text, not
/// a JSON envelope — the caller (see <c>SessionStartAdditionalContext</c>)
/// joins this fragment with other fragments (e.g. the version-upgrade nudge)
/// and serializes a single Claude Code <c>hookSpecificOutput</c> envelope.
/// </summary>
static class SessionGuidelinesEmitter {
    /// <summary>
    /// Returns the lessons block text, or <c>null</c> when there is nothing
    /// to emit (no <c>top_clusters</c>, all empty, user opted out, malformed
    /// response).
    /// </summary>
    /// <param name="responseNode">The hook response body parsed as a <see cref="JsonNode"/>.</param>
    /// <param name="disabled">True when the user has set <c>disable_session_guidelines</c> on their active profile.</param>
    public static string? BuildFragment(JsonNode? responseNode, bool disabled) {
        if (disabled) return null;
        if (responseNode is not JsonObject obj) return null;
        if (obj["top_clusters"] is not JsonArray topClusters || topClusters.Count == 0) return null;

        var lines = new List<string>(topClusters.Count);

        foreach (var node in topClusters) {
            string? text = null;

            try {
                text = node?["text"]?.GetValue<string>();
            } catch {
                // Tolerate non-string/missing text entries.
            }

            if (!string.IsNullOrWhiteSpace(text)) lines.Add($"- {text}");
        }

        if (lines.Count == 0) return null;

        return "Recurring lessons from prior sessions in this repo (no action required unless relevant):\n"
             + string.Join("\n", lines);
    }
}
