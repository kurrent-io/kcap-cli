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

            return Hook.Parse(node) switch {
                Hook.ClearEnded end     => Recorded(end, body, config, agentPid),
                Hook.ClearStarted start => Linked(start, config, agentPid) ?? body,
                _                       => body,
            };
        } catch {
            // Best effort. Without the link the server falls back to its own owner+cwd note.
            return body;
        }
    }

    static string Recorded(Hook.ClearEnded end, string body, ConfigRoot config, Func<int?> agentPid) {
        if (agentPid() is { } pid) Record(config, pid, end.SessionId);

        return body;
    }

    static string? Linked(Hook.ClearStarted start, ConfigRoot config, Func<int?> agentPid) =>
        agentPid() is { } pid && Take(config, pid) is { } cleared ? start.LinkedTo(cleared) : null;

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

    /// <summary>What a Claude hook body means to a clear link.</summary>
    abstract record Hook {
        public sealed record ClearEnded(string SessionId) : Hook;

        public sealed record ClearStarted(JsonObject Body) : Hook {
            public string LinkedTo(string previousSessionId) {
                Body["previous_session_id"] = previousSessionId;

                return Body.ToJsonString();
            }
        }

        public sealed record Unrelated : Hook;

        public static Hook Parse(JsonObject body) => (Str(body, "hook_event_name"), Str(body, "reason"), Str(body, "source")) switch {
            ("SessionEnd", "clear", _) when Str(body, "session_id") is { Length: > 0 } id => new ClearEnded(id.Replace("-", "")),
            ("SessionStart", _, "clear") when body["previous_session_id"] is null        => new ClearStarted(body),
            _                                                                           => new Unrelated(),
        };

        static string? Str(JsonObject body, string key) =>
            body[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }
}
