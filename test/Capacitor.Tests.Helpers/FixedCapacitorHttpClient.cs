using Capacitor.Cli.Core.Http;

namespace Capacitor.Tests.Helpers;

/// <summary>Hands back a fresh, unauthenticated <see cref="HttpClient"/> per call — for driving an
/// API implementation (<c>FeedbackApi</c>, <c>SessionsApi</c>, ...) directly against a stub server,
/// bypassing the auth-discovery pipeline <c>AddCapacitorHttp</c> wires in production.</summary>
public sealed class FixedCapacitorHttpClient : ICapacitorHttpClient {
    public Task<HttpClient> ForCommandAsync(CancellationToken ct = default) => Task.FromResult(new HttpClient());
}
