using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Http;
using Microsoft.Extensions.DependencyInjection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// <summary>
/// The sign-in lane as the container configures it, and the request shape github.com is picky about.
/// </summary>
public class GitHubOAuthClientTests : IDisposable {
    readonly WireMockServer  _server = WireMockServer.Start();
    readonly ServiceProvider _sp     = new ServiceCollection().AddCapacitorForeignClients().BuildServiceProvider();

    public void Dispose() {
        _server.Stop();
        _sp.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The exchange body carries the authorization code and the PKCE verifier, so a 307 — which
    /// preserves method and body, unlike a 302 — must end it rather than re-send them to whatever host
    /// the Location names. A client built without the lane's configuration follows the hop, so only
    /// resolving the registered one proves this.
    /// </summary>
    [Test]
    public async Task A_redirect_from_the_code_exchange_does_not_re_send_the_verifier() {
        _server.Given(Request.Create().WithPath("/code-exchange").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(307)
                .WithHeader("Location", $"{_server.Urls[0]}/harvested"));

        _server.Given(Request.Create().WithPath("/harvested").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"access_token":"gho_harvested"}"""));

        using var response = await _sp.GetRequiredService<GitHubOAuthClient>().ExchangeCodeAsync(
            $"{_server.Urls[0]}/code-exchange",
            new GitHubCodeExchangeRequest { Code = "the_code", CodeVerifier = "the_verifier", RedirectUri = "http://127.0.0.1:1/" },
            CancellationToken.None);

        await Assert.That(_server.LogEntries.Any(e => e.RequestMessage.Path == "/harvested")).IsFalse()
            .Because("the code and the verifier are in the body — a hop would hand them onward");
    }

    /// <summary>
    /// github.com answers a form-encoded body unless the request asks for JSON, and nothing downstream
    /// parses that. The scopes are the other half: the exchange reads org membership, which
    /// <c>read:user</c> alone does not grant.
    /// </summary>
    [Test]
    public async Task The_device_code_request_asks_for_json_and_carries_the_org_scope() {
        using var capture = new CapturingHandler();

        using var response = await new GitHubOAuthClient(new PlainHttpClientFactory(capture))
            .RequestDeviceCodeAsync("Iv1.abc", CancellationToken.None);

        await Assert.That(capture.Accept).Contains("application/json");
        await Assert.That(capture.Body).Contains("scope=read%3Auser+read%3Aorg");
        await Assert.That(capture.Body).Contains("client_id=Iv1.abc");
    }

    sealed class CapturingHandler : HttpMessageHandler {
        public string? Accept { get; private set; }
        public string  Body   { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            Accept = request.Headers.Accept.ToString();
            Body   = await request.Content!.ReadAsStringAsync(ct);

            return new(System.Net.HttpStatusCode.OK) {
                Content = new StringContent("""{"device_code":"dc"}""", System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
}
