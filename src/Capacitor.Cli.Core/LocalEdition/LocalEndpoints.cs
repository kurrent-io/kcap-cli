namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>
/// The installation registry as endpoint resolution sees it: which server URLs are the local
/// installation, and the one spelling each of them resolves to.
///
/// <para>The installation's origin can be written <c>localhost</c>, <c>127.0.0.1</c> or <c>[::1]</c>.
/// Every one of them is rewritten to the installation's canonical URL before anything is keyed on it,
/// so one stack never becomes several server identities, and a native request always carries the
/// authority the server's Host policy expects.</para>
/// </summary>
public sealed class LocalEndpoints(StateRead<InstallRegistry> registry) {
    public static LocalEndpoints None { get; } = new(new StateRead<InstallRegistry>.Absent());

    public static LocalEndpoints Read(LocalRoot root) => new(LocalStateFiles.ReadRegistry(root));

    public StateRead<InstallRegistry> Registry { get; } = registry;

    public LocalInstallation? Active => Registry is StateRead<InstallRegistry>.Present { Value.Active: { } active } ? active : null;

    public LocalEndpointClass Classify(string? url) {
        if (LoopbackPort(url) is not { } port) return new LocalEndpointClass.Ordinary();

        switch (Registry) {
            case StateRead<InstallRegistry>.Unreadable(var path, var reason):
                return new LocalEndpointClass.Undetermined($"the local installation registry {path} cannot be read ({reason})");

            case StateRead<InstallRegistry>.Present(var registry):
                if (registry.Active is { } active && active.Port == port) return new LocalEndpointClass.Local(active);
                if (registry.Retired.FirstOrDefault(r => r.Port == port) is { } retired) return new LocalEndpointClass.Retired(retired);
                break;
        }

        return new LocalEndpointClass.Ordinary();
    }

    /// <summary>The URL with the active installation's origin in its canonical spelling, path kept;
    /// any other URL unchanged.</summary>
    public string? Canonicalize(string? url) {
        if (Classify(url) is not LocalEndpointClass.Local(var installation)) return url;

        var path = new Uri(url!).AbsolutePath.TrimEnd('/');

        return installation.ServerUrl.TrimEnd('/') + path;
    }

    /// <summary>The port of a plain-http URL on one of the three loopback spellings, or null. A URL with
    /// user info, a query or a fragment is not a server base; it is left for validation to reject
    /// rather than rewritten into one.</summary>
    static int? LoopbackPort(string? url) {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp) return null;
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) return null;

        return uri.Host is "localhost" or "127.0.0.1" or "[::1]" ? uri.Port : null;
    }
}
