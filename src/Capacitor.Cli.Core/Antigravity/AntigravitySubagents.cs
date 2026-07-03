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
    /// messages. Best-effort: unreadable / malformed message files are skipped. A child that
    /// somehow maps to multiple parents keeps the first seen (deterministic by brain-dir order).
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildParentMap(string? home = null, string? geminiCliHome = null) {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        var brainRoot = Path.Combine(AntigravityPaths.Root(home, geminiCliHome), "brain");
        if (!Directory.Exists(brainRoot)) return map;

        foreach (var brainDir in Directory.EnumerateDirectories(brainRoot)) {
            var messages = Path.Combine(brainDir, ".system_generated", "messages");
            if (!Directory.Exists(messages)) continue;

            foreach (var file in Directory.EnumerateFiles(messages, "*.json")) {
                try {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;

                    var sender    = root.TryGetProperty("sender",    out var s) ? s.GetString() : null;
                    var recipient = root.TryGetProperty("recipient", out var r) ? r.GetString() : null;

                    if (!string.IsNullOrEmpty(sender)
                     && !string.IsNullOrEmpty(recipient)
                     && !string.Equals(sender, recipient, StringComparison.Ordinal)
                     && !map.ContainsKey(sender!)) {
                        map[sender!] = recipient!;
                    }
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
}
