using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.Muse;

namespace Capacitor.Cli.Commands.Harness;

/// <summary>
/// The config root and server a <c>kcap hook --muse</c> process runs against. Muse runs a hook with a
/// cleared environment, so a hook for a session hosted by a daemon with a non-default config root would
/// resolve the home's default profile; the session's marker names the daemon's root and server instead.
/// Each applies only where the environment sets none. Resolved at the composition root, before anything
/// reads the root.
/// </summary>
sealed record MuseHookConnection(string? ConfigDir, string? ServerUrl) {
    public static readonly MuseHookConnection None = new(null, null);

    /// <summary>A child's hook carries its own id as <c>session_id</c>, so the marker is looked up under
    /// the top-level session <c>MUSE_SESSION_ID</c> names, falling back to the payload's.</summary>
    public static MuseHookConnection Resolve(string payload, UserHome home, Func<string, string?> env) {
        var sessionId = MuseHookCommand.ParentSessionId(env) ?? PayloadSessionId(payload);
        if (sessionId is null || MuseHostedSessionMarker.TryRead(home, sessionId) is not { } marker) return None;

        var dir = string.IsNullOrEmpty(env(ConfigRoot.ConfigDirEnvVar))
               && marker.ConfigDir is { } d && Path.IsPathFullyQualified(d)
            ? d
            : null;

        var url = string.IsNullOrEmpty(env(ProfileOverrides.UrlVar))
               && Uri.TryCreate(marker.ServerUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? marker.ServerUrl
            : null;

        return new(dir, url);
    }

    public ConfigRoot ConfigRootOr(Func<ConfigRoot> fromEnvironment) =>
        ConfigDir is { } dir ? new ConfigRoot(dir) : fromEnvironment();

    public ProfileOverrides Apply(ProfileOverrides fromEnvironment) =>
        ServerUrl is null ? fromEnvironment : fromEnvironment with { Url = ServerUrl };

    static string? PayloadSessionId(string payload) {
        try {
            return JsonNode.Parse(payload)?["session_id"] is JsonValue v && v.TryGetValue<string>(out var s)
                && Guid.TryParse(s, out var id)
                ? id.ToString("N")
                : null;
        } catch (JsonException) {
            return null;
        }
    }
}
