using System.Text;
using System.Text.Json.Nodes;

namespace Capacitor.Cli;

/// <summary>
/// builds the SessionStart "team memory" index
/// fragment from the <c>GET /api/memories/index</c> response body: a JSON array of
/// <c>{memory_id, slug, audience, description, kind}</c>, already capped and
/// most-recently-updated first by the server.
/// <para>
/// Entries are grouped by <c>audience</c> (org → team → user), preserving the
/// server's order within each group, and rendered as <c>slug: description</c> — one
/// line per memory — under a lead-in that tells the agent to call
/// <c>get_memory</c> / <c>search_memories</c> for full content. Bodies are NEVER
/// injected: this mirrors a local <c>MEMORY.md</c> index so the injected token cost
/// stays roughly flat as the pool grows.
/// </para>
/// <para>
/// Returns <c>null</c> when disabled, empty, or malformed — the caller
/// (<c>SessionStartAdditionalContext</c>) drops null fragments, so a failed or empty
/// fetch emits nothing (fail-open, same as guideline injection).
/// </para>
/// </summary>
static class MemoryIndexEmitter {
    /// <param name="indexNode">The <c>/api/memories/index</c> response body parsed as a <see cref="JsonNode"/> (expected: a JSON array).</param>
    /// <param name="disabled">True when the user set <c>disable_memory_index</c> on their active profile.</param>
    public static string? BuildFragment(JsonNode? indexNode, bool disabled) {
        if (disabled) return null;
        if (indexNode is not JsonArray entries || entries.Count == 0) return null;

        // Preserve server order (updated_at DESC) within each audience bucket.
        var org  = new List<string>();
        var team = new List<string>();
        var user = new List<string>();

        foreach (var node in entries) {
            if (node is not JsonObject o) continue;

            string? slug, description, audience;
            try {
                slug        = o["slug"]?.GetValue<string>();
                description = o["description"]?.GetValue<string>();
                audience    = o["audience"]?.GetValue<string>();
            } catch {
                continue; // skip a malformed entry rather than dropping the whole block
            }

            if (string.IsNullOrWhiteSpace(slug) || string.IsNullOrWhiteSpace(description)) continue;

            // Collapse any internal whitespace — including newlines — to single spaces so a
            // description keeps to one bullet. The server validates descriptions as single-line,
            // but the injected block feeds an LLM's context, so the CLI must not depend on that:
            // a stray '\n' would otherwise split one memory across lines and distort the grouping.
            var line = $"- {OneLine(slug)}: {OneLine(description)}";
            switch (audience) {
                case "org":  org.Add(line);  break;
                case "team": team.Add(line); break;
                case "user": user.Add(line); break;
                // Unknown/missing audience: skip — we only render the three known buckets,
                // and grouping on an unrecognized key would be misleading.
            }
        }

        if (org.Count == 0 && team.Count == 0 && user.Count == 0) return null;

        var sb = new StringBuilder();
        sb.AppendLine("## Team memory");
        sb.AppendLine("Durable memories for this repo/context. Call `get_memory <slug>` for the full content of any entry, or `search_memories` to find more.");

        AppendGroup(sb, "Org", org);
        AppendGroup(sb, "Team", team);
        AppendGroup(sb, "Yours", user);

        return sb.ToString().TrimEnd();
    }

    static void AppendGroup(StringBuilder sb, string heading, List<string> lines) {
        if (lines.Count == 0) return;

        sb.AppendLine();
        sb.AppendLine($"### {heading}");
        foreach (var l in lines) sb.AppendLine(l);
    }

    // Collapse all whitespace runs (spaces, tabs, CR/LF) to single spaces and trim, so any
    // value renders as one line. Splitting on null splits on Unicode whitespace.
    static string OneLine(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
