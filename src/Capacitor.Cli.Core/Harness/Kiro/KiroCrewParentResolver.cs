using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Harness.Kiro;

/// <summary>
/// Finds the Kiro session that spawned a Kiro Crew sub-agent. Crew gives the child's hooks nothing
/// about its parent, so the link is joined from Crew's own files: the sub-agent's record names its
/// Kiro session, its parent chat and when it started; the session map names that chat's current and
/// previous Kiro sessions; and each Kiro session's metadata says when it was created.
/// </summary>
/// <remarks>
/// A chat can move to a new Kiro session after spawning a child, so the parent is the newest of the
/// chat's sessions created before the child started. When neither mapped session qualifies — the
/// chat has moved more than once since — no parent is named rather than a wrong one.
/// </remarks>
public static class KiroCrewParentResolver {
    /// <summary>Bounds the live hook's scan on a machine with a long sub-agent history; the newest are
    /// checked first.</summary>
    public const int LiveScanLimit = 256;

    /// <summary>The most children the server takes on one session-start; more go out in further batches.</summary>
    public const int MaxChildrenPerStart = 64;

    /// <summary>Crew's records are a few KB; anything far larger is not one and is not parsed.</summary>
    const long MaxRecordBytes = 1024 * 1024;

    /// <summary>The parent's dashed session id, or null when the session is not a Crew sub-agent or Crew
    /// has not recorded it yet. Scans at most <see cref="LiveScanLimit"/> sub-agents.</summary>
    public static string? ParentOf(KiroCrewPaths crew, string sessionsDir, string sessionId) {
        if (!Guid.TryParse(sessionId, out var child) || !crew.IsPresent()) return null;

        foreach (var record in SubagentRecords(crew, LiveScanLimit)) {
            if (record.Session != child) continue;

            return ParentFor(record, ReadObject(crew.SessionMapJson), sessionsDir);
        }

        return null;
    }

    /// <summary>The dashed ids of the recorded sub-agents this session spawned, newest first. Scans at
    /// most <see cref="LiveScanLimit"/> sub-agents.</summary>
    public static IReadOnlyList<string> ChildrenOf(KiroCrewPaths crew, string sessionsDir, string sessionId) {
        if (!Guid.TryParse(sessionId, out var parent) || !crew.IsPresent()) return [];

        var map      = ReadObject(crew.SessionMapJson);
        var children = new List<string>();

        foreach (var record in SubagentRecords(crew, LiveScanLimit)) {
            if (ParentFor(record, map, sessionsDir) is { } p && Guid.Parse(p) == parent) children.Add(record.Session.ToString("D"));
        }

        return children;
    }

    /// <summary>Every recorded sub-agent's parent, keyed by the child's session, for a historical import
    /// that must reach records of any age.</summary>
    public static IReadOnlyDictionary<Guid, string> AllParents(KiroCrewPaths crew, string sessionsDir) {
        if (!crew.IsPresent()) return FrozenDictionary<Guid, string>.Empty;

        var map     = ReadObject(crew.SessionMapJson);
        var parents = new Dictionary<Guid, string>();

        foreach (var record in SubagentRecords(crew, int.MaxValue)) {
            if (parents.ContainsKey(record.Session)) continue;
            if (ParentFor(record, map, sessionsDir) is { } parent) parents[record.Session] = parent;
        }

        return parents;
    }

    /// <summary>Whether the session is, or was, a Crew chat's own session rather than a sub-agent's.</summary>
    public static bool IsChatSession(KiroCrewPaths crew, string sessionId) {
        if (!Guid.TryParse(sessionId, out var id) || ReadObject(crew.SessionMapJson) is not { } map) return false;

        foreach (var (_, node) in map) {
            if (node is not JsonObject entry) continue;
            if (GuidOf(entry, "sid") == id || GuidOf(entry, "discarded_sid") == id) return true;
        }

        return false;
    }

    readonly record struct SubagentRecord(Guid Session, string Chat, DateTimeOffset Started);

    static IEnumerable<SubagentRecord> SubagentRecords(KiroCrewPaths crew, int limit) {
        List<DirectoryInfo> dirs;

        try {
            dirs = new DirectoryInfo(crew.SubagentsDir).EnumerateDirectories()
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .Take(limit)
                .ToList();
        } catch {
            yield break;
        }

        foreach (var dir in dirs) {
            // A finished sub-agent keeps its state.json; tombstone.json covers one whose state is gone or incomplete.
            if ((RecordFrom(Path.Combine(dir.FullName, "state.json")) ?? RecordFrom(Path.Combine(dir.FullName, "tombstone.json"))) is { } record)
                yield return record;
        }
    }

    static SubagentRecord? RecordFrom(string path) =>
        ReadObject(path) is { } obj
     && GuidOf(obj, "session_id") is { } session
     && StringOf(obj, "parent_session") is { Length: > 0 } chat
     && EpochOf(obj, "started") is { } started
            ? new SubagentRecord(session, chat, started)
            : null;

    static string? ParentFor(SubagentRecord record, JsonObject? map, string sessionsDir) {
        if (map?[record.Chat] is not JsonObject entry) return null;

        Guid?           parent  = null;
        DateTimeOffset? created = null;

        foreach (var candidate in new[] { GuidOf(entry, "sid"), GuidOf(entry, "discarded_sid") }) {
            if (candidate is not { } id || id == record.Session) continue;
            if (CreatedAt(sessionsDir, id) is not { } at || at > record.Started) continue;
            if (created is { } newest && at <= newest) continue;

            parent  = id;
            created = at;
        }

        return parent?.ToString("D");
    }

    static DateTimeOffset? CreatedAt(string sessionsDir, Guid session) =>
        ReadObject(Path.Combine(sessionsDir, $"{session:D}.json")) is { } meta
     && StringOf(meta, "created_at") is { } raw
     && DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : null;

    /// <summary>Reads a file an agent may be rewriting: shared read-write, so the reader never blocks
    /// Crew's own write on Windows, and anything unparseable (a half-written file) is simply absent.</summary>
    static JsonObject? ReadObject(string path) {
        try {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxRecordBytes) return null;

            using var reader = new StreamReader(stream);

            return JsonNode.Parse(reader.ReadToEnd()) as JsonObject;
        } catch {
            return null;
        }
    }

    static string? StringOf(JsonObject obj, string key) =>
        obj[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static Guid? GuidOf(JsonObject obj, string key) =>
        Guid.TryParse(StringOf(obj, key), out var g) ? g : null;

    static DateTimeOffset? EpochOf(JsonObject obj, string key) {
        if (obj[key] is not JsonValue v || !v.TryGetValue<double>(out var seconds) || double.IsNaN(seconds) || double.IsInfinity(seconds)) return null;

        try {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
        } catch (ArgumentOutOfRangeException) {
            return null;
        }
    }
}
