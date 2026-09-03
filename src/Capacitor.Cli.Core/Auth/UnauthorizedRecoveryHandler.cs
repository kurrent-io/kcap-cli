using System.Net;
using System.Net.Http.Headers;

namespace Capacitor.Cli.Core.Auth;

/// <summary>
/// Recovers from a single 401 by rotating the credential and resending once — a bearer can be
/// locally valid yet already refused. Only ONE component may own 401-retry on a client, or one
/// rejection multiplies into several rotations, so only the client-construction choke point installs it.
/// </summary>
internal sealed class UnauthorizedRecoveryHandler(ICredentialSource source) : DelegatingHandler {
    // Swapped whole, never mutated in place, so concurrent requests see one bearer or the other.
    string? _current;

    /// <summary>The bearer a caller has already resolved, so the first send needs no second read.</summary>
    public string? InitialBearer { init => _current = value; }

    protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) {
        // The handler's own bearer, not the client's default header, which still carries the refused one.
        var applied = Volatile.Read(ref _current) ?? await SeedAsync(cancellationToken);

        if (applied is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", applied);

        var response = await base.SendAsync(request, cancellationToken);

        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;
        if (applied is null) return response; // Nothing was sent, so there is nothing to rotate against.
        if (!CanResend(request)) return response;

        // `applied`, not a re-read: a peer may have rotated already, and blaming its fresh credential
        // would discard one the server never refused.
        var rotated = await source.RotateAsync(applied, cancellationToken);

        if (rotated.Bearer is null) return response;

        Volatile.Write(ref _current, rotated.Bearer);
        response.Dispose();

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", rotated.Bearer);

        // base.SendAsync, not recursion: exactly one extra attempt, so a second 401 reaches the caller.
        return await base.SendAsync(request, cancellationToken);
    }

    // A registered client resolves on first send, because the container cannot resolve it at build
    // time. Concurrent first sends may both resolve; they publish the same bearer.
    async Task<string?> SeedAsync(CancellationToken ct) {
        var state = await source.ResolveAsync(ct);

        if (state.Bearer is null) return null;

        Interlocked.CompareExchange(ref _current, state.Bearer, null);

        return Volatile.Read(ref _current);
    }

    // Only a body that re-serializes can be replayed; a stream-backed one is consumed by the first
    // attempt. JsonContent must stay listed or the JSON-posting call sites lose recovery.
    static bool CanResend(HttpRequestMessage request) =>
        request.Content is null or ByteArrayContent or System.Net.Http.Json.JsonContent;
}
