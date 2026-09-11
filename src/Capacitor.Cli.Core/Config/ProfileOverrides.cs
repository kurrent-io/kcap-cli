namespace Capacitor.Cli.Core.Config;

/// <summary>
/// What the environment says about which server this process talks to, resolved once at the
/// composition root: a URL that outranks every profile, and a profile name that outranks repo
/// discovery. <see cref="ProfileResolver"/> owns what they outrank; this owns what counts as set.
///
/// <para>The names are a contract between the two executables and the service unit — kcap stamps
/// them onto the watchers, agents and units it spawns, and each of those reads them back — so a
/// name that drifts on one side fails silently. Both sides take them from here.</para>
/// </summary>
public sealed record ProfileOverrides(string? Url, string? Profile) {
    public const string UrlVar     = "KCAP_URL";
    public const string ProfileVar = "KCAP_PROFILE";

    /// <summary>
    /// An exported variable can be empty, and an empty value names no server. Discarded on the way
    /// in — on every construction path, not just <see cref="FromEnvironment"/> — so a caller testing
    /// this for null cannot disagree with <see cref="ProfileResolver"/>, which skips an empty value
    /// and lets the next input win. A value of blanks was set by someone and still counts as one.
    /// </summary>
    public string? Url { get => _url; init => _url = Named(value); }

    /// <inheritdoc cref="Url"/>
    public string? Profile { get => _profile; init => _profile = Named(value); }

    readonly string? _url     = Named(Url);
    readonly string? _profile = Named(Profile);

    static string? Named(string? value) => value is { Length: > 0 } ? value : null;

    /// <summary>Nothing overridden — selection falls through to the repo and the active profile.</summary>
    public static readonly ProfileOverrides None = new(null, null);

    /// <summary>This process's overrides. Call once, in <c>Main</c> or the composition root.</summary>
    public static ProfileOverrides FromEnvironment() =>
        new(Environment.GetEnvironmentVariable(UrlVar), Environment.GetEnvironmentVariable(ProfileVar));
}
