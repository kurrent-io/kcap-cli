using System.Globalization;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Harness.Claude;

/// <summary>
/// Pairs a <c>/clear</c>'s SessionEnd with the SessionStart that follows it. Both hooks run under
/// the same Claude process, which is the only thing that tells two terminals in one folder apart.
/// </summary>
static class ClaudeClearLink {
    /// <summary>A clear SessionEnd records its session against the Claude process; a clear
    /// SessionStart takes that record back as <c>previous_session_id</c>. Every other body, and any
    /// failure, passes the body through unchanged.</summary>
    public static string Apply(string body, ConfigRoot config, Func<int?> agentPid) {
        if (!body.Contains("clear", StringComparison.OrdinalIgnoreCase)) return body;

        try {
            if (JsonNode.Parse(body) is not JsonObject node) return body;

            var ev = Str(node, "hook_event_name")?.Replace("_", "").Replace("-", "");

            if (string.Equals(ev, "SessionEnd", StringComparison.OrdinalIgnoreCase)
             && string.Equals(Str(node, "reason"), "clear", StringComparison.OrdinalIgnoreCase)
             && Str(node, "session_id") is { Length: > 0 } ended
             && agentPid() is { } endPid) {
                Record(config, endPid, ended.Replace("-", ""));
                return body;
            }

            if (string.Equals(ev, "SessionStart", StringComparison.OrdinalIgnoreCase)
             && string.Equals(Str(node, "source"), "clear", StringComparison.OrdinalIgnoreCase)
             && node["previous_session_id"] is null
             && agentPid() is { } startPid
             && Take(config, startPid) is { } cleared) {
                node["previous_session_id"] = cleared;
                return node.ToJsonString();
            }
        } catch {
            // Best effort. Without the link the server falls back to its own owner+cwd note.
        }

        return body;
    }

    static string? Str(JsonObject node, string key) =>
        node[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static string NotePath(ConfigRoot config, int pid) => config.Path("clear-links", pid.ToString(CultureInfo.InvariantCulture));

    static void Record(ConfigRoot config, int pid, string sessionId) {
        var path = NotePath(config, pid);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $"{sessionId}\n{ProcessStartToken.ForPid(pid)}");
    }

    /// <summary>Reads and removes the note. It is refused unless it carries the start token of the
    /// process holding the pid now, so a note left by an earlier holder never links.</summary>
    static string? Take(ConfigRoot config, int pid) {
        var path = NotePath(config, pid);
        if (!File.Exists(path)) return null;

        var lines = File.ReadAllText(path).Split('\n');
        File.Delete(path);

        var recordedToken = lines.Length > 1 ? lines[1].Trim() : "";
        if (recordedToken.Length == 0 || ProcessStartToken.ForPid(pid) != recordedToken) return null;

        return lines[0].Trim() is { Length: > 0 } sessionId ? sessionId : null;
    }
}
