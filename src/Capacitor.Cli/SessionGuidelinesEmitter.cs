using System.Text;
using System.Text.Json.Nodes;

namespace Capacitor.Cli;

/// <summary>
/// Builds the session guidelines text fragment from a server
/// <c>/hooks/session-start</c> response body. Returns plain text, not
/// a JSON envelope — the caller (see <c>SessionStartAdditionalContext</c>)
/// joins this fragment with other fragments (e.g. the version-upgrade nudge)
/// and serializes a single Claude Code <c>hookSpecificOutput</c> envelope.
/// <para>
/// <c>top_clusters</c> entries are grouped by <c>category</c>:
/// <c>agent_guidance</c> entries render under <b>"Guidance from past sessions"</b>;
/// everything else renders under <b>"Known patterns"</b>.
/// Only non-empty blocks are emitted.
/// </para>
/// </summary>
static class SessionGuidelinesEmitter {
    /// <summary>
    /// Returns the guidelines block text, or <c>null</c> when there is nothing
    /// to emit (no <c>top_clusters</c>, all empty, user opted out, malformed
    /// response).
    /// </summary>
    /// <param name="responseNode">The hook response body parsed as a <see cref="JsonNode"/>.</param>
    /// <param name="disabled">True when the user has set <c>disable_session_guidelines</c> on their active profile.</param>
    public static string? BuildFragment(JsonNode? responseNode, bool disabled) {
        if (disabled) return null;
        if (responseNode is not JsonObject obj) return null;
        if (obj["top_clusters"] is not JsonArray topClusters || topClusters.Count == 0) return null;

        var patterns = new List<string>();
        var guidance = new List<string>();

        foreach (var node in topClusters) {
            if (node is null) continue;

            string? text;
            try { text = node["text"]?.GetValue<string>(); }
            catch { continue; }

            if (string.IsNullOrWhiteSpace(text)) continue;

            string? category;
            try { category = node["category"]?.GetValue<string>(); }
            catch { category = null; }

            var target = category == "agent_guidance" ? guidance : patterns;
            target.Add($"- {text}");
        }

        if (patterns.Count == 0 && guidance.Count == 0) return null;

        var sb = new StringBuilder();

        if (patterns.Count > 0) {
            sb.AppendLine("## Known patterns");
            foreach (var l in patterns) sb.AppendLine(l);
        }

        if (guidance.Count > 0) {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine("## Guidance from past sessions");
            foreach (var l in guidance) sb.AppendLine(l);
        }

        return sb.ToString().TrimEnd();
    }
}
