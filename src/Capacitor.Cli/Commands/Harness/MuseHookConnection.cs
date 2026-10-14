using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.Muse;

namespace Capacitor.Cli.Commands.Harness;

/// <summary>
/// The config root and server a <c>kcap hook --muse</c> process runs against. Muse runs a hook with a
/// cleared environment, so a hook for a hosted session would resolve the home's root and its own
/// profile, not the daemon's; the session's marker names the daemon's instead. The root applies unless
/// the environment names one, and the server unless the environment chooses one by URL or profile.
/// Resolved at the composition root, before anything reads the root.
/// </summary>
sealed record MuseHookConnection(string? ConfigDir, string? ServerUrl, string? Profile) {
    public static readonly MuseHookConnection None = new(null, null, null);

    /// <summary>A child's hook carries its own id as <c>session_id</c>, so the marker is looked up under
    /// the top-level session <c>MUSE_SESSION_ID</c> names, falling back to the payload's.</summary>
    public static MuseHookConnection Resolve(string payload, UserHome home, Func<string, string?> env) {
        var sessionId = MuseHookCommand.ParentSessionId(env) ?? PayloadSessionId(payload);
        if (sessionId is null || MuseHostedSessionMarker.TryRead(home, sessionId) is not { } marker) return None;

        var dir = string.IsNullOrEmpty(env(ConfigRoot.ConfigDirEnvVar))
               && marker.ConfigDir is { } d && Path.IsPathFullyQualified(d)
            ? d
            : null;

        if (!string.IsNullOrEmpty(env(ProfileOverrides.UrlVar)) || !string.IsNullOrEmpty(env(ProfileOverrides.ProfileVar)))
            return new(dir, null, null);

        // The profile carries the credential; its server is the one the daemon resolved through it.
        if (marker.Profile is { Length: > 0 } profile) return new(dir, null, profile);

        var url = Uri.TryCreate(marker.ServerUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? marker.ServerUrl
            : null;

        return new(dir, url, null);
    }

    public ConfigRoot ConfigRootOr(Func<ConfigRoot> fromEnvironment) =>
        ConfigDir is { } dir ? new ConfigRoot(dir) : fromEnvironment();

    public ProfileOverrides Apply(ProfileOverrides fromEnvironment) =>
        Profile is not null ? fromEnvironment with { Profile = Profile }
      : ServerUrl is not null ? fromEnvironment with { Url = ServerUrl }
      : fromEnvironment;

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
