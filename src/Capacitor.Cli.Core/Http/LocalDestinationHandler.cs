using System.Net;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Core.Http;

/// <summary>
/// Refuses every send to a purged local installation's origin. With a root, the registry is read at each loopback
/// send, not at resolution only: a process that resolved the installation before its purge must not go on delivering
/// to whatever takes the port afterwards. Without one, only the refusal made at resolution applies, and only to
/// requests for that server's origin.
///
/// <para>With <paramref name="followRedirects"/> it follows redirects itself, checking each hop before it is sent, as
/// the runtime would otherwise follow them below it unchecked. It must then be the innermost handler, over a primary
/// handler that follows none, and it follows them as the runtime does: Authorization dropped when a hop crosses
/// origin, no hop from https to http, and a POST turned into a GET on 300 to 303.</para>
/// </summary>
public sealed class LocalDestinationHandler(LocalRoot? root, string? serverUrl, string? resolvedRefusal, bool followRedirects = false)
    : DelegatingHandler {
    /// <summary>The runtime's own default.</summary>
    const int MaxRedirects = 50;

    public LocalDestinationHandler(CapacitorServer server, bool followRedirects = false)
        : this(server.LocalRoot, server.Url, server.Refusal, followRedirects) { }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
        Check(request.RequestUri);

        var response = await base.SendAsync(request, ct);

        for (var hops = 0; followRedirects && hops < MaxRedirects; hops++) {
            if (Next(request, response) is not { } target) break;

            Check(target);

            if (ForcesGet(response.StatusCode, request.Method)) {
                request.Method  = HttpMethod.Get;
                request.Content = null;
            }

            if (!SameOrigin(request.RequestUri!, target)) request.Headers.Authorization = null;

            request.RequestUri = target;
            response.Dispose();

            response = await base.SendAsync(request, ct);
        }

        return response;
    }

    void Check(Uri? uri) {
        if (uri is not null && LocalDestination.Refusal(uri, root, serverUrl, resolvedRefusal) is { } refusal)
            throw new LocalDestinationException(refusal);
    }

    static Uri? Next(HttpRequestMessage request, HttpResponseMessage response) {
        if (response.StatusCode is not (HttpStatusCode.MultipleChoices or HttpStatusCode.MovedPermanently or HttpStatusCode.Found
                or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
            return null;

        if (response.Headers.Location is not { } location || request.RequestUri is not { } from) return null;

        var target = location.IsAbsoluteUri ? location : new Uri(from, location);

        if (target.Scheme != Uri.UriSchemeHttp && target.Scheme != Uri.UriSchemeHttps) return null;
        if (from.Scheme == Uri.UriSchemeHttps && target.Scheme == Uri.UriSchemeHttp) return null;

        return target;
    }

    static bool ForcesGet(HttpStatusCode status, HttpMethod method) =>
        status switch {
            HttpStatusCode.MultipleChoices or HttpStatusCode.MovedPermanently or HttpStatusCode.Found => method == HttpMethod.Post,
            HttpStatusCode.SeeOther                                                                     => method != HttpMethod.Get && method != HttpMethod.Head,
            _                                                                                           => false,
        };

    static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
     && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase)
     && a.Port == b.Port;
}
