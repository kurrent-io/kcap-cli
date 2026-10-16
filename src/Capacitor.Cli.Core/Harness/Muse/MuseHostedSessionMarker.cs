using System.Text.Json;
using System.Text.Json.Serialization;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core.Harness.Muse;

/// <summary>
/// Links a Muse session to the daemon-hosted agent running it, for a hook that inherits no
/// <c>KCAP_AGENT_ID</c>. Muse runs a hook with a cleared environment, so the hook knows only the home:
/// the marker lives under the config root derived from it, whatever <c>KCAP_CONFIG_DIR</c> the daemon
/// runs under.
/// </summary>
/// <param name="ConfigDir">The daemon's config root, fully qualified, when it is not the one the home
/// derives; null otherwise.</param>
/// <param name="ServerUrl">The server the daemon talks to.</param>
/// <param name="Profile">The profile the daemon resolved its server through; null when an explicit URL
/// chose it.</param>
/// <param name="SessionKind">What the daemon launched the session as, in the wire form of
/// <see cref="HostedSessionKinds"/> — the hook's only source for it, since no environment reaches it.</param>
/// <param name="FlowRole">The participant's flow role, when the launch named one.</param>
/// <remarks>The connection and kind fields are absent from a marker an older daemon wrote, and read as null.</remarks>
public sealed partial record MuseHostedSessionMarker(
        string         SessionId,
        string         AgentId,
        string         DaemonId,
        string         DaemonEpoch,
        DateTimeOffset WrittenAt,
        string?        ConfigDir = null,
        string?        ServerUrl = null,
        string?        Profile   = null,
        string?        SessionKind = null,
        string?        FlowRole    = null
    ) {
    public static string DirectoryFor(UserHome home) => ConfigRoot.UnderHome(home.Path).Path("muse-hosted");

    public static string PathFor(UserHome home, string sessionId) =>
        Path.Join(DirectoryFor(home), Dashless(sessionId) + ".json");

    public static void Write(UserHome home, MuseHostedSessionMarker marker) {
        var path = PathFor(home, marker.SessionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.Replace(path, JsonSerializer.Serialize(marker, MarkerJsonCtx.Default.MuseHostedSessionMarker));
    }

    /// <summary>Null when the marker is missing, malformed, or the id is not a UUID.</summary>
    public static MuseHostedSessionMarker? TryRead(UserHome home, string sessionId) {
        try {
            return JsonSerializer.Deserialize(
                File.ReadAllText(PathFor(home, sessionId)), MarkerJsonCtx.Default.MuseHostedSessionMarker);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException) {
            return null;
        }
    }

    public static void Delete(UserHome home, string sessionId) {
        try { File.Delete(PathFor(home, sessionId)); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException) {
            /* best-effort */
        }
    }

    /// <summary>Removes markers written longer ago than <paramref name="olderThan"/>: leftovers of a
    /// daemon that crashed before its hook could delete them. A marker that cannot be parsed ages by its
    /// file time.</summary>
    public static void Sweep(UserHome home, TimeProvider time, TimeSpan olderThan) {
        var dir = DirectoryFor(home);
        if (!Directory.Exists(dir)) return;

        var cutoff = time.GetUtcNow() - olderThan;

        try {
            foreach (var file in Directory.EnumerateFiles(dir, "*.json")) SweepOne(file, cutoff);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            /* the directory itself is unreadable */
        }
    }

    /// <summary>One unreadable or undeletable marker is skipped, never the rest of the sweep.</summary>
    static void SweepOne(string file, DateTimeOffset cutoff) {
        try {
            try {
                var marker  = JsonSerializer.Deserialize(File.ReadAllText(file), MarkerJsonCtx.Default.MuseHostedSessionMarker);
                var written = marker?.WrittenAt ?? File.GetLastWriteTimeUtc(file);
                if (written < cutoff) File.Delete(file);
            } catch (JsonException) {
                if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
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
