using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Harness.Kiro;

/// <summary>
/// Finds the Kiro session that spawned a Kiro Crew sub-agent. Crew gives the child's hooks nothing
/// about its parent, so the link is joined from Crew's own files: the sub-agent's record names its
/// Kiro session and its parent chat, and the session map names that chat's Kiro session.
/// </summary>
public static class KiroCrewParentResolver {
    /// <summary>Bounds the scan on a machine with a long sub-agent history; the newest are checked first.</summary>
    const int MaxSubagentsScanned = 256;

    /// <summary>The parent's dashed session id, or null when the session is not a Crew sub-agent or
    /// Crew has not recorded it yet.</summary>
    public static string? ParentOf(KiroCrewPaths crew, string sessionId) {
        if (!Guid.TryParse(sessionId, out var child) || !crew.IsPresent()) return null;
        if (FindParentChat(crew, child) is not { } chat) return null;
        if (ReadSessionMap(crew)?[chat] is not JsonObject entry) return null;

        return StringOf(entry, "sid") is { } sid && Guid.TryParse(sid, out var parent) && parent != child
            ? parent.ToString("D")
            : null;
    }

    /// <summary>Whether the session is, or was, a Crew chat's own session rather than a sub-agent's.</summary>
    public static bool IsChatSession(KiroCrewPaths crew, string sessionId) {
        if (!Guid.TryParse(sessionId, out var id) || ReadSessionMap(crew) is not { } map) return false;

        foreach (var (_, node) in map) {
            if (node is not JsonObject entry) continue;
            if (SameSession(StringOf(entry, "sid"), id) || SameSession(StringOf(entry, "discarded_sid"), id)) return true;
        }

        return false;
    }

    static string? FindParentChat(KiroCrewPaths crew, Guid child) {
        IEnumerable<DirectoryInfo> dirs;

        try {
            dirs = new DirectoryInfo(crew.SubagentsDir).EnumerateDirectories()
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .Take(MaxSubagentsScanned)
                .ToList();
        } catch {
            return null;
        }

        foreach (var dir in dirs) {
            // A finished sub-agent keeps its state.json; tombstone.json covers one whose state was cleaned up.
            foreach (var file in new[] { "state.json", "tombstone.json" }) {
                if (ReadObject(Path.Combine(dir.FullName, file)) is not { } record) continue;
                if (!SameSession(StringOf(record, "session_id"), child)) break;

                return StringOf(record, "parent_session") is { Length: > 0 } chat ? chat : null;
            }
        }

        return null;
    }

    static JsonObject? ReadSessionMap(KiroCrewPaths crew) => ReadObject(crew.SessionMapJson);

    static JsonObject? ReadObject(string path) {
        try {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null;
        } catch {
            return null;
        }
    }

    static string? StringOf(JsonObject obj, string key) =>
        obj[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static bool SameSession(string? candidate, Guid id) => Guid.TryParse(candidate, out var g) && g == id;
}
