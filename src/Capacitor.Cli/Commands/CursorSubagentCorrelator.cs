using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Correlates Cursor subagent sessions to their parent (AI-1153).
///
/// Cursor runs a subagent (a <c>Task</c>/<c>Agent</c> tool call) as its OWN
/// top-level session with its own transcript — and provides NO explicit
/// parent→child id anywhere in the transcript. The only reliable in-data link
/// is that the child session's first <c>user_query</c> (minus Cursor's
/// <c>&lt;user_query&gt;…&lt;/user_query&gt;</c> wrapper) is byte-identical to the
/// parent's <c>Task</c>/<c>Agent</c> tool_use <c>input.prompt</c>.
///
/// This recovers that link (keyed by a SHA-256 of the trimmed prompt) so the
/// importer can ingest the child under the parent's <c>AgentSubsession</c> stream
/// (via <c>/hooks/subagent-start</c> + transcript-with-agent_id) instead of as a
/// separate top-level session. Pure over the on-disk transcripts — no server or
/// read-model state.
/// </summary>
public static class CursorSubagentCorrelator {
    public readonly record struct SubagentLink(string ParentSessionId, string? SubagentType);

    /// <summary>
    /// Returns a map of <c>childSessionId → (parentSessionId, subagentType)</c> for every
    /// discovered session whose first user prompt matches another session's Task prompt.
    /// Sessions with no match are absent from the map. A session is never linked to itself.
    /// </summary>
    public static Dictionary<string, SubagentLink> Correlate(
        IEnumerable<(string SessionId, string TranscriptPath)> sessions
    ) {
        var scanned = new List<(string Sid, string? FirstUserHash, List<(string Hash, string? SubType)> Tasks)>();

        // Order by session id so "first writer wins" (below) is deterministic — the caller
        // feeds us filesystem-discovery order, which is not stable across runs; without a
        // stable tie-break, a duplicate Task prompt could route the same child under a
        // different parent on re-run and defeat idempotency.
        foreach (var (sid, path) in sessions.OrderBy(s => s.SessionId, StringComparer.Ordinal)) {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
            try {
                var firstHash = ScanFirstUserHash(path, out var tasks);
                scanned.Add((sid, firstHash, tasks));
            } catch {
                // A single unreadable/locked transcript must not abort correlation (which would
                // abort ClassifyAsync for the whole import) — skip it and correlate the rest.
            }
        }

        // promptHash → (parentSessionId, subagentType). First writer wins on the (rare)
        // case of two parents Tasking an identical prompt.
        var parentByPrompt = new Dictionary<string, (string Parent, string? SubType)>(StringComparer.Ordinal);
        foreach (var s in scanned)
            foreach (var (hash, subType) in s.Tasks)
                parentByPrompt.TryAdd(hash, (s.Sid, subType));

        var links = new Dictionary<string, SubagentLink>(StringComparer.Ordinal);
        foreach (var s in scanned) {
            if (s.FirstUserHash is null) continue;
            if (parentByPrompt.TryGetValue(s.FirstUserHash, out var p)
             && !string.Equals(p.Parent, s.Sid, StringComparison.Ordinal)) {
                links[s.Sid] = new SubagentLink(p.Parent, p.SubType);
            }
        }

        return links;
    }

    /// <summary>
    /// Scans one transcript: returns the hash of its first user prompt (wrapper-stripped)
    /// and collects the hashes of every <c>Task</c>/<c>Agent</c> tool_use prompt it issues.
    /// </summary>
    static string? ScanFirstUserHash(string path, out List<(string Hash, string? SubType)> tasks) {
        tasks = [];
        string? firstUserHash = null;

        foreach (var line in File.ReadLines(path)) {
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); } catch { continue; }

            using (doc) {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (!root.TryGetProperty("role", out var roleEl)) continue;
                var role = roleEl.GetString();
                if (!root.TryGetProperty("message", out var msg)
                 || !msg.TryGetProperty("content", out var content)
                 || content.ValueKind != JsonValueKind.Array) continue;

                if (role == "user" && firstUserHash is null) {
                    foreach (var b in content.EnumerateArray()) {
                        if (b.ValueKind == JsonValueKind.Object
                         && b.TryGetProperty("type", out var ty) && ty.GetString() == "text"
                         && b.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String) {
                            firstUserHash = Hash(StripUserQueryWrapper(tx.GetString() ?? ""));
                            break;
                        }
                    }
                } else if (role == "assistant") {
                    foreach (var b in content.EnumerateArray()) {
                        if (b.ValueKind != JsonValueKind.Object) continue;
                        if (!b.TryGetProperty("type", out var ty) || ty.GetString() != "tool_use") continue;
                        var name = b.TryGetProperty("name", out var n) ? n.GetString() : null;
                        if (name is not ("Task" or "Agent")) continue;
                        if (!b.TryGetProperty("input", out var inp) || inp.ValueKind != JsonValueKind.Object) continue;
                        if (!inp.TryGetProperty("prompt", out var pr) || pr.ValueKind != JsonValueKind.String) continue;
                        var prompt = pr.GetString();
                        if (string.IsNullOrWhiteSpace(prompt)) continue;
                        var subType = inp.TryGetProperty("subagent_type", out var st) ? st.GetString() : null;
                        tasks.Add((Hash(prompt), subType));
                    }
                }
            }
        }

        return firstUserHash;
    }

    static string StripUserQueryWrapper(string text) {
        var t = text.Trim();
        const string open = "<user_query>", close = "</user_query>";
        return t.StartsWith(open, StringComparison.Ordinal) && t.EndsWith(close, StringComparison.Ordinal)
            ? t[open.Length..^close.Length].Trim()
            : t;
    }

    static string Hash(string s) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s.Trim())));
}
