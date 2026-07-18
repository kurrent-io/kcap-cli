using System.Text.Json;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Per-vendor `ended_at` resolution. The rule is NOT one blanket helper:
/// vendors whose records carry durable timestamps use a tail-scan; Copilot's best end is the
/// final `session.shutdown` record (which the import-relevance helper deliberately skips —
/// this resolver must NOT inherit that skip contract); Cursor has no durable record timestamp
/// so its caller documents a fallback (last user-wrapper timestamp else mtime).
/// </summary>
internal static class EndedAtResolvers {
    /// <summary>Scan the JSONL tail for the last record carrying <paramref name="timestampField"/>.</summary>
    public static DateTimeOffset? LastTimestampFromJsonl(string path, string timestampField = "timestamp") {
        try {
            DateTimeOffset? last = null;
            foreach (var line in File.ReadLines(path)) {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (TryTimestamp(line, timestampField) is { } ts) last = ts;
            }
            return last;
        } catch {
            return null;
        }
    }

    /// <summary>Copilot: the session-final `session.shutdown` record's timestamp, else null.</summary>
    public static DateTimeOffset? CopilotShutdownTimestamp(string path) {
        try {
            DateTimeOffset? shutdown = null;
            foreach (var line in File.ReadLines(path)) {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("type", out var t)
                     && t.ValueKind == JsonValueKind.String && t.GetString() == "session.shutdown"
                     && doc.RootElement.TryGetProperty("timestamp", out var ts)
                     && ts.ValueKind == JsonValueKind.String
                     && DateTimeOffset.TryParse(ts.GetString(), out var parsed))
                        shutdown = parsed;
                } catch (JsonException) { }
            }
            return shutdown;
        } catch {
            return null;
        }
    }

    static DateTimeOffset? TryTimestamp(string line, string field) {
        try {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty(field, out var ts)
                && ts.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(ts.GetString(), out var parsed)
                ? parsed : null;
        } catch (JsonException) {
            return null;
        }
    }
}
