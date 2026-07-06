using System.Text.Json;

namespace Capacitor.Cli.Core.Antigravity;

/// <summary>
/// Resolves Antigravity's subagent → parent conversation linkage for historical import
/// (AI-1160). A subagent is a SEPARATE conversation; the parent's brain dir records the
/// linkage as inter-agent messages under
/// <c>brain/&lt;parent&gt;/.system_generated/messages/*.json</c>, each
/// <c>{ "sender": "&lt;child&gt;", "recipient": "&lt;parent&gt;" }</c>. This linkage is only
/// written when a child reports back, so it is unreliable at live child-start (nesting is
/// deferred there) — but at IMPORT time every message file is on disk, so the full map is
/// resolvable. Roots are conversations that are never a <c>sender</c>.
/// </summary>
public static class AntigravitySubagents {
    /// <summary>
    /// Builds child-conversation-id → parent-conversation-id by scanning every brain dir's
    /// messages. Best-effort: unreadable / malformed message files are skipped. If a child maps
    /// to MULTIPLE parents (pathological / non-tree data), the lexicographically-smallest parent
    /// wins so the map is deterministic regardless of directory enumeration order.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildParentMap(
            string? home = null, string? geminiCliHome = null, CancellationToken ct = default) {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        var brainRoot = Path.Combine(AntigravityPaths.Root(home, geminiCliHome), "brain");
        if (!Directory.Exists(brainRoot)) return map;

        // Building the full map requires scanning every brain dir's messages (a child records its
        // parent, so finding any root's descendants means reading all links). That's O(history) IO,
        // so honour cancellation between dirs and files — a targeted `--session` import or a Ctrl+C
        // must be able to interrupt it (AI-1160 review).
        foreach (var brainDir in Directory.EnumerateDirectories(brainRoot)) {
            ct.ThrowIfCancellationRequested();
            var messages = Path.Combine(brainDir, ".system_generated", "messages");
            if (!Directory.Exists(messages)) continue;

            foreach (var file in Directory.EnumerateFiles(messages, "*.json")) {
                ct.ThrowIfCancellationRequested();
                try {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;

                    var sender    = root.TryGetProperty("sender",    out var s) ? s.GetString() : null;
                    var recipient = root.TryGetProperty("recipient", out var r) ? r.GetString() : null;

                    if (string.IsNullOrEmpty(sender)
                     || string.IsNullOrEmpty(recipient)
                     || string.Equals(sender, recipient, StringComparison.Ordinal))
                        continue;

                    // Deterministic on conflict: keep the smallest parent (Ordinal).
                    if (!map.TryGetValue(sender!, out var existing)
                     || string.CompareOrdinal(recipient!, existing) < 0)
                        map[sender!] = recipient!;
                } catch {
                    // Skip a malformed / unreadable message file — never abort the whole map.
                }
            }
        }

        return map;
    }

    /// <summary>Parent conversation id for a child, or null when it is a root / unlinked.</summary>
    public static string? ResolveParent(string childConversationId, string? home = null, string? geminiCliHome = null) =>
        BuildParentMap(home, geminiCliHome).TryGetValue(childConversationId, out var parent) ? parent : null;

    /// <summary>
    /// Live-watcher scoped scan (AI-1218): children that have reported back to
    /// <paramref name="parentSessionId"/> so far, read from ONLY that parent's own
    /// <c>messages/*.json</c> dir (unlike <see cref="BuildParentMap"/>, which scans every brain
    /// dir to build the full cross-conversation map for import). A live parent watcher polls
    /// this each drain to detect newly-linked subagents while its own conversation is still
    /// running. Best-effort: unreadable / malformed message files are skipped; deduped;
    /// self-messages excluded.
    /// </summary>
    public static IReadOnlyList<string> ChildrenOf(
            string parentSessionId, string? home = null, string? geminiCliHome = null, CancellationToken ct = default) {
        var children = new HashSet<string>(StringComparer.Ordinal);

        var messages = AntigravityPaths.MessagesDir(parentSessionId, home, geminiCliHome);
        if (!Directory.Exists(messages)) return [];

        foreach (var file in Directory.EnumerateFiles(messages, "*.json")) {
            ct.ThrowIfCancellationRequested();
            try {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;

                var sender    = root.TryGetProperty("sender",    out var s) ? s.GetString() : null;
                var recipient = root.TryGetProperty("recipient", out var r) ? r.GetString() : null;

                if (string.IsNullOrEmpty(sender)
                 || string.IsNullOrEmpty(recipient)
                 || !string.Equals(recipient, parentSessionId, StringComparison.Ordinal)
                 || string.Equals(sender, parentSessionId, StringComparison.Ordinal))
                    continue;

                children.Add(sender!);
            } catch {
                // Skip a malformed / unreadable message file — never abort the scan.
            }
        }

        return [.. children];
    }

    /// <summary>
    /// Walks parent links up to the top-level ancestor (a conversation with no parent), so a
    /// deep chain P←C←G resolves G's ancestor to P. Cycle-safe: if a cycle is encountered
    /// (A↔B, or a child that is also its own ancestor) the starting conversation is returned as
    /// its own ancestor, so cycle members import standalone rather than being silently lost.
    /// A conversation with no parent returns itself.
    /// </summary>
    public static string ResolveTopLevelAncestor(string convId, IReadOnlyDictionary<string, string> parentMap) {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cur  = convId;
        while (parentMap.TryGetValue(cur, out var parent)) {
            if (!seen.Add(cur)) return convId; // cycle → treat the origin as its own root
            cur = parent;
        }
        return cur;
    }

    /// <summary>
    /// Groups every conversation under its top-level ancestor: returns rootConvId → all
    /// transitive descendant convIds (children, grandchildren, …). Keys are the roots (import
    /// as sessions); each value list is imported as subagents of that root. Non-tree edges are
    /// handled by <see cref="ResolveTopLevelAncestor"/>. <paramref name="allConversationIds"/>
    /// is the full set of brain-dir conversation ids (a root with no messages still appears).
    /// </summary>
    public static IReadOnlyDictionary<string, List<string>> BuildRootDescendants(
            IEnumerable<string> allConversationIds, IReadOnlyDictionary<string, string> parentMap) {
        var byRoot = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var convId in allConversationIds) {
            var ancestor = ResolveTopLevelAncestor(convId, parentMap);
            if (!byRoot.TryGetValue(ancestor, out var list))
                byRoot[ancestor] = list = [];
            if (!string.Equals(convId, ancestor, StringComparison.Ordinal))
                list.Add(convId); // a descendant of its ancestor root
        }

        return byRoot;
    }
}
