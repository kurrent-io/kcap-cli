namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>Why nothing may be sent to a server URL, measured against the installation registry: a purged
/// installation's origin, or a loopback origin while the registry cannot be read. Whatever listens on such a
/// port now is not the server the URL was configured for.</summary>
public static class LocalDestination {
    /// <summary>The refusal for <paramref name="endpoint"/>, or null when it may be sent to.</summary>
    public static string? Refusal(string? url, LocalEndpointClass endpoint) =>
        endpoint switch {
            LocalEndpointClass.Retired(var retired) =>
                $"{url ?? retired.ServerUrl} belonged to the local installation {retired.Id}, which was removed with kcap local uninstall --purge; nothing is sent to it. Point the profile at a server with kcap setup.",
            LocalEndpointClass.Undetermined(var reason) =>
                $"Cannot tell whether {url} is the local installation, so nothing is sent to it: {reason}.",
            _ => null,
        };

    /// <summary>The refusal for a request to <paramref name="uri"/>, reading the registry now. Only a loopback
    /// request reads it.</summary>
    public static string? Refusal(Uri uri, LocalRoot root) {
        if (!uri.IsAbsoluteUri || !uri.IsLoopback) return null;

        var origin = uri.GetLeftPart(UriPartial.Authority);

        return Refusal(origin, LocalEndpoints.Read(root).Classify(origin));
    }
}
