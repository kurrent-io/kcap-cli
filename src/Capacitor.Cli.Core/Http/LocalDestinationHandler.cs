using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Core.Http;

/// <summary>Refuses every send to a purged local installation's origin. With a root, the registry is read at each
/// loopback send, not at resolution only: a process that resolved the installation before its purge must not go on
/// delivering to whatever takes the port afterwards. Without one, only the refusal made at resolution applies, and
/// only to requests for that server's origin.</summary>
public sealed class LocalDestinationHandler(LocalRoot? root, string? serverUrl, string? resolvedRefusal) : DelegatingHandler {
    public LocalDestinationHandler(CapacitorServer server) : this(server.LocalRoot, server.Url, server.Refusal) { }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
        if (request.RequestUri is { } uri && LocalDestination.Refusal(uri, root, serverUrl, resolvedRefusal) is { } refusal)
            throw new LocalDestinationException(refusal);

        return base.SendAsync(request, ct);
    }
}
