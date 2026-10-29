using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Harness.Muse;

/// <summary>
/// Finds and labels the child sessions Muse nests under a parent log
/// (<c>&lt;session&gt;/subagent/&lt;childSessionId&gt;/session.jsonl</c>). A child's own log never says what it
/// is; the parent log does, in two shapes: a worker is announced by <c>spawn_accepted</c> then
/// <c>child_session_bound</c> (joined on <c>subagent_id</c>), a reminder observer by
/// <c>memory_reminder_child_session_linked</c>.
/// </summary>
public static class MuseSubagentDiscovery {
    const string DefaultWorkerType = "worker";
    const string UnlabelledType    = "subagent";

    static readonly string[] Announcements = ["spawn_accepted", "child_session_bound", "memory_reminder_child_session_linked"];

    /// <summary>Child logs that exist, in id order. A directory whose name is not a Guid is not a Muse child.</summary>
    public static IReadOnlyList<string> EnumerateChildLogs(string parentLogPath) {
        if (Path.GetDirectoryName(parentLogPath) is not { } sessionDir) return [];

        var subagentDir = Path.Combine(sessionDir, "subagent");
        if (!Directory.Exists(subagentDir)) return [];

        var logs = new List<string>();
        try {
            foreach (var dir in Directory.EnumerateDirectories(subagentDir)) {
                var id = Path.GetFileName(dir);
                if (!Guid.TryParse(id, out _)) continue;

                var log = MusePaths.ChildSessionLogPath(parentLogPath, id);
                if (File.Exists(log)) logs.Add(log);
            }
        } catch (IOException) { } catch (UnauthorizedAccessException) { }

        logs.Sort(StringComparer.Ordinal);
        return logs;
    }

    /// <summary>Labels keyed by the child's dashed session id. Muse appends to the parent log while this
    /// reads it, so a torn last line is skipped, not an error.</summary>
    public static IReadOnlyDictionary<string, MuseChildLabel> ResolveChildLabels(string parentLogPath) {
        var spawns = new Dictionary<string, (string? Role, string? AgentPath)>(StringComparer.Ordinal);
        var bound  = new List<(string SubagentId, string ChildId)>();
        var labels = new Dictionary<string, MuseChildLabel>(StringComparer.Ordinal);

        try {
            using var stream = new FileStream(parentLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            while (reader.ReadLine() is { } line) {
                if (!Announcements.Any(a => line.Contains(a, StringComparison.Ordinal))) continue;

                try {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                    if (!doc.RootElement.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) continue;

                    if (Inner(payload, "event") is { } ev && Text(ev, "kind") == "memory_reminder_child_session_linked") {
                        if (Canonical(Text(ev, "child_session_id")) is { } childId && Text(ev, "reminder_agent_id") is { Length: > 0 } reminder)
                            labels[childId] = Label(childId, reminder);
                    } else if (Inner(payload, "record") is { } rec) {
                        switch (Text(rec, "kind")) {
                            case "spawn_accepted" when Text(rec, "subagent_id") is { Length: > 0 } sid:
                                spawns[sid] = (Text(rec, "role"), Text(rec, "agent_path"));
                                break;
                            case "child_session_bound" when Text(rec, "subagent_id") is { Length: > 0 } sid
                                                        && Canonical(Text(rec, "child_session_id")) is { } childId:
                                bound.Add((sid, childId));
                                break;
                        }
                    }
                } catch (JsonException) { }
            }
        } catch (IOException) { } catch (UnauthorizedAccessException) { }

        foreach (var (subagentId, childId) in bound) {
            var type = spawns.TryGetValue(subagentId, out var s)
                ? NonEmpty(s.Role) ?? NonEmpty(s.AgentPath) ?? DefaultWorkerType
                : DefaultWorkerType;
            labels[childId] = Label(childId, type);
        }

        return labels.Count == 0 ? FrozenDictionary<string, MuseChildLabel>.Empty : labels;
    }

    /// <summary>How many times the parent log reports each child's worker finished (<c>result_ready</c>,
    /// joined to its child through <c>child_session_bound</c>), keyed by dashed child session id.</summary>
    public static IReadOnlyDictionary<string, int> ResolveResultReadyCounts(string parentLogPath) {
        var ready = new Dictionary<string, int>(StringComparer.Ordinal);
        var bound = new Dictionary<string, string>(StringComparer.Ordinal);

        try {
            using var stream = new FileStream(parentLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            while (reader.ReadLine() is { } line) {
                if (!line.Contains("\"result_ready\"", StringComparison.Ordinal)
                 && !line.Contains("\"child_session_bound\"", StringComparison.Ordinal)) continue;

                try {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                    if (!doc.RootElement.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) continue;
                    if (Inner(payload, "record") is not { } rec || Text(rec, "subagent_id") is not { Length: > 0 } sid) continue;

                    switch (Text(rec, "kind")) {
                        case "result_ready": ready[sid] = ready.GetValueOrDefault(sid) + 1; break;
                        case "child_session_bound" when Canonical(Text(rec, "child_session_id")) is { } childId: bound[sid] = childId; break;
                    }
                } catch (JsonException) { }
            }
        } catch (IOException) { } catch (UnauthorizedAccessException) { }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (sid, count) in ready) {
            if (bound.TryGetValue(sid, out var childId)) counts[childId] = count;
        }

        return counts.Count == 0 ? FrozenDictionary<string, int>.Empty : counts;
    }

    /// <summary>The label for a child found on disk; one the parent log never announced is a plain
    /// <c>subagent</c>. A non-Guid id is reduced to its letters and digits so nothing path-shaped is registered.</summary>
    public static MuseChildLabel LabelFor(string childSessionId, IReadOnlyDictionary<string, MuseChildLabel> labels) {
        if (Canonical(childSessionId) is { } canonical)
            return labels.TryGetValue(canonical, out var known) ? known : Label(canonical, UnlabelledType);

        var safe = new string(childSessionId.Where(char.IsAsciiLetterOrDigit).ToArray());
        return new MuseChildLabel(safe, safe, UnlabelledType);
    }

    /// <summary>A reminder observer is announced to hooks by name (<c>skill-reminder</c>), a worker by its Guid.</summary>
    public static bool IsReminderAgentId(string subagentId) => !Guid.TryParse(subagentId, out _);

    public static JsonObject BuildStartPayload(string parentSessionId, string agentId, string agentType, string childLogPath) =>
        new() {
            ["hook_event_name"] = "subagent_start",
            ["session_id"]      = parentSessionId,
            ["agent_id"]        = agentId,
            ["agent_type"]      = agentType,
            ["transcript_path"] = childLogPath,
            ["cwd"]             = "",
        };

    public static JsonObject BuildStopPayload(string parentSessionId, string agentId, string agentType, string childLogPath) =>
        new() {
            ["hook_event_name"]        = "subagent_stop",
            ["session_id"]             = parentSessionId,
            ["agent_id"]               = agentId,
            ["agent_type"]             = agentType,
            ["transcript_path"]        = childLogPath,
            ["cwd"]                    = "",
            ["stop_hook_active"]       = false,
            ["agent_transcript_path"]  = childLogPath,
            ["last_assistant_message"] = "",
        };

    static MuseChildLabel Label(string dashedChildId, string agentType) =>
        new(dashedChildId, Guid.Parse(dashedChildId).ToString("N"), agentType);

    static string? Canonical(string? id) => Guid.TryParse(id, out var guid) ? guid.ToString("D") : null;

    static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    static JsonElement? Inner(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var inner) && inner.ValueKind == JsonValueKind.Object ? inner : null;

    static string? Text(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
