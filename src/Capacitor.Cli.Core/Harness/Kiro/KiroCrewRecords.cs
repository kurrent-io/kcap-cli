using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Harness.Kiro;

/// <summary>Reads Kiro Crew's JSON state files, which Crew may be rewriting while they are read.</summary>
static class KiroCrewRecords {
    /// <summary>Crew's records are a few KB; anything far larger is not one and is not parsed.</summary>
    const long MaxRecordBytes = 1024 * 1024;

    /// <summary>Shared read-write, so the reader never blocks Crew's own write on Windows; anything
    /// unparseable (a half-written file) is simply absent.</summary>
    public static JsonObject? Read(string path) {
        try {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxRecordBytes) return null;

            using var reader = new StreamReader(stream);

            return JsonNode.Parse(reader.ReadToEnd()) as JsonObject;
        } catch {
            return null;
        }
    }

    public static string? StringOf(JsonObject obj, string key) =>
        obj[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static Guid? GuidOf(JsonObject obj, string key) =>
        Guid.TryParse(StringOf(obj, key), out var g) ? g : null;

    public static DateTimeOffset? EpochOf(JsonObject obj, string key) {
        if (obj[key] is not JsonValue v || !v.TryGetValue<double>(out var seconds) || double.IsNaN(seconds) || double.IsInfinity(seconds)) return null;

        try {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
        } catch (ArgumentOutOfRangeException) {
            return null;
        }
    }
}
