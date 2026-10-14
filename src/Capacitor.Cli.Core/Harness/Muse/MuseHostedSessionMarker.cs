using System.Text.Json;
using System.Text.Json.Serialization;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core.Harness.Muse;

/// <summary>
/// Links a Muse session to the daemon-hosted agent running it, for a hook that inherits no
/// <c>KCAP_AGENT_ID</c>. The hook resolves <see cref="ConfigRoot.FromEnvironment"/> with a cleared
/// environment, so a daemon running under a non-default <c>KCAP_CONFIG_DIR</c> writes where the hook
/// never looks; the relay path carries the link there instead.
/// </summary>
public sealed partial record MuseHostedSessionMarker(
        string         SessionId,
        string         AgentId,
        string         DaemonId,
        string         DaemonEpoch,
        DateTimeOffset WrittenAt
    ) {
    public static string PathFor(ConfigRoot config, string sessionId) =>
        config.Path("muse-hosted", Dashless(sessionId) + ".json");

    public static void Write(ConfigRoot config, MuseHostedSessionMarker marker) {
        var path = PathFor(config, marker.SessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.Replace(path, JsonSerializer.Serialize(marker, MarkerJsonCtx.Default.MuseHostedSessionMarker));
    }

    /// <summary>Null when the marker is missing, malformed, or the id is not a UUID.</summary>
    public static MuseHostedSessionMarker? TryRead(ConfigRoot config, string sessionId) {
        try {
            return JsonSerializer.Deserialize(
                File.ReadAllText(PathFor(config, sessionId)), MarkerJsonCtx.Default.MuseHostedSessionMarker);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException) {
            return null;
        }
    }

    public static void Delete(ConfigRoot config, string sessionId) {
        try { File.Delete(PathFor(config, sessionId)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) {
            /* best-effort */
        }
    }

    /// <summary>Removes markers written longer ago than <paramref name="olderThan"/>: leftovers of a
    /// daemon that crashed before its hook could delete them. A marker that cannot be parsed ages by its
    /// file time.</summary>
    public static void Sweep(ConfigRoot config, TimeProvider time, TimeSpan olderThan) {
        var dir = config.Path("muse-hosted");
        if (!Directory.Exists(dir)) return;

        var cutoff = time.GetUtcNow() - olderThan;

        try {
            foreach (var file in Directory.EnumerateFiles(dir, "*.json")) {
                try {
                    var marker  = JsonSerializer.Deserialize(File.ReadAllText(file), MarkerJsonCtx.Default.MuseHostedSessionMarker);
                    var written = marker?.WrittenAt ?? File.GetLastWriteTimeUtc(file);
                    if (written < cutoff) File.Delete(file);
                } catch (JsonException) {
                    if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
                }
            }
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            /* best-effort */
        }
    }

    static string Dashless(string sessionId) =>
        Guid.TryParse(sessionId, out var id) ? id.ToString("N") : throw new FormatException("A Muse session id is a UUID.");

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
    [JsonSerializable(typeof(MuseHostedSessionMarker))]
    partial class MarkerJsonCtx : JsonSerializerContext;
}
