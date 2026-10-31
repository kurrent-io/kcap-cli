using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Core.Http;

/// <summary>
/// The one Capacitor server this process talks to, plus the context reaching it needs. A handler the
/// factory constructs cannot be handed per-call arguments, so this carries them as a singleton.
/// </summary>
/// <param name="localRoot">The local installation's root, so every send can be checked against its registry.
/// Without one, only the classification made at resolution is enforced.</param>
public sealed class CapacitorServer(string? url, ConfigRoot config, ProfileContext profiles, LocalRoot? localRoot = null) {
    /// <summary>
    /// Without a trailing slash, because every caller builds a path by interpolating onto it: a
    /// configured <c>https://host/</c> would otherwise reach the server as <c>//api/...</c>. Empty
    /// when nothing resolved one, which <see cref="Usable"/> already refuses.
    /// </summary>
    public string Url { get; } = url?.TrimEnd('/') ?? "";

    public ConfigRoot     Config    { get; } = config;
    public ProfileContext Profiles  { get; } = profiles;
    public LocalRoot?     LocalRoot { get; } = localRoot;

    /// <summary>Why <see cref="Url"/> is a removed local installation, or may be one; null otherwise.</summary>
    public string? Refusal { get; } = LocalDestination.Refusal(url, Classify(url, profiles, localRoot));

    public bool IsLocal { get; } = Classify(url, profiles, localRoot) is LocalEndpointClass.Local;

    /// <summary>
    /// Whether <see cref="Url"/> can be sent to at all. Evaluated once here rather than per request:
    /// a send path that re-asks has to decide what to do about it mid-request, which is how validation
    /// ends up ending the process. The send itself is checked again by <see cref="LocalDestinationHandler"/>.
    /// </summary>
    public bool Usable => Refusal is null && _postable;

    /// <summary>What to tell the user when <see cref="Usable"/> is false.</summary>
    public string UnusableHint => Refusal ?? HttpClientExtensions.SchemeMissingHint;

    readonly bool _postable = HookHttp.IsPostable(url);

    static LocalEndpointClass Classify(string? url, ProfileContext profiles, LocalRoot? localRoot) =>
        localRoot is not null ? LocalEndpoints.Read(localRoot).Classify(url) : profiles.Resolution.Endpoint;
}
