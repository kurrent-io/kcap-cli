using System.Net;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// <summary>A server <c>/auth/refresh</c> that mints <paramref name="freshToken"/> and records which
/// access token each refresh presented.</summary>
sealed class RefreshEndpointStub(string freshToken) : HttpMessageHandler {
    readonly Lock _gate = new();

    public List<string> Presented { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        var sent = body is null ? null : JsonNode.Parse(body)?["access_token"]?.GetValue<string>();

        lock (_gate) Presented.Add(sent ?? "<none>");

        return new(HttpStatusCode.OK) {
            Content = new StringContent($$"""{"access_token":"{{freshToken}}","expires_in":3600}""",
                                        System.Text.Encoding.UTF8, "application/json")
        };
    }
}
