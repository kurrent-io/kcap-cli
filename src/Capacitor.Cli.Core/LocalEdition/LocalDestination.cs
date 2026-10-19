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

    /// <summary>The refusal for a request to <paramref name="uri"/>: the registry under <paramref name="root"/> read
    /// now, when there is one; otherwise <paramref name="resolvedRefusal"/>, for a request to
    /// <paramref name="serverUrl"/>'s origin only.</summary>
    public static string? Refusal(Uri uri, LocalRoot? root, string? serverUrl, string? resolvedRefusal) {
        if (!uri.IsAbsoluteUri) return null;

        // A WebSocket upgrade goes to the server's own origin under its ws spelling.
        var scheme = uri.Scheme switch { "ws" => Uri.UriSchemeHttp, "wss" => Uri.UriSchemeHttps, var other => other };
        var origin = $"{scheme}://{uri.Authority}";

        if (root is not null)
            return uri.IsLoopback ? Refusal(origin, LocalEndpoints.Read(root).Classify(origin)) : null;

        return resolvedRefusal is not null
            && Uri.TryCreate(serverUrl, UriKind.Absolute, out var server)
            && string.Equals(server.GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase)
                ? resolvedRefusal
                : null;
    }

    /// <summary>Throws <see cref="LocalDestinationException"/> when <paramref name="uri"/> is refused, for a transport
    /// that does not go through a handler, such as a WebSocket.</summary>
    public static void Check(Uri uri, LocalRoot? root, string? serverUrl, string? resolvedRefusal) {
        if (Refusal(uri, root, serverUrl, resolvedRefusal) is { } refusal) throw new LocalDestinationException(refusal);
    }
}
