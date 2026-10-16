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

        var url = Uri.TryCreate(marker.ServerUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? marker.ServerUrl
            : null;

        return new(dir, url, marker.Profile is { Length: > 0 } profile ? profile : null);
    }

    public ConfigRoot ConfigRootOr(Func<ConfigRoot> fromEnvironment) =>
        ConfigDir is { } dir ? new ConfigRoot(dir) : fromEnvironment();

    public ProfileOverrides Apply(ProfileOverrides fromEnvironment) =>
        fromEnvironment with { Url = ServerUrl ?? fromEnvironment.Url, Profile = Profile ?? fromEnvironment.Profile };

    /// <summary>A URL outranks a profile and drops its credential. When the marker pins both, the URL stays
    /// the destination and the profile still supplies the credential.</summary>
    public ProfileContext Bind(ProfileContext resolved) =>
        Profile is { } name && ServerUrl is not null && resolved.Snapshot.Profiles.TryGetValue(name, out var profile)
            ? new(resolved.Resolution with { ProfileName = name, Profile = profile }, resolved.Snapshot)
            : resolved;

    /// <summary>Set when the marker's profile is gone: the credential would fall back to the active
    /// profile's and record the session under another user, so the hook must not post or spool.</summary>
    public string? MissingProfileWarning(ProfileContext resolved) =>
        Profile is { } name && !resolved.Snapshot.Profiles.ContainsKey(name)
            ? $"[kcap] muse hook skipped: profile '{name}', which this session was started under, no longer exists."
            : null;

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
