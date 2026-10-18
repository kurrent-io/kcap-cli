using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Core.Http;

/// <summary>Refuses every send to a purged local installation's origin. The registry is read at each loopback
/// send, not at resolution only: a process that resolved the installation before its purge must not go on
/// delivering to whatever takes the port afterwards.</summary>
internal sealed class LocalDestinationHandler(CapacitorServer server) : DelegatingHandler {
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
        var refusal = server.LocalRoot is { } root && request.RequestUri is { } uri
            ? LocalDestination.Refusal(uri, root)
            : server.Refusal;

        if (refusal is not null) throw new LocalDestinationException(refusal);

        return base.SendAsync(request, ct);
    }
}
