using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Http;
using Microsoft.Extensions.DependencyInjection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// <summary>
/// The WorkOS lane as the container actually configures it. A client built by hand follows redirects,
/// so nothing short of resolving the registered one proves what the registration contributes.
/// </summary>
public class WorkOSClientTests : IDisposable {
    readonly WireMockServer  _server = WireMockServer.Start();
    readonly ServiceProvider _sp     = new ServiceCollection().AddCapacitorForeignClients().BuildServiceProvider();

    public void Dispose() {
        _server.Stop();
        _sp.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The mint carries the client secret in the body, so a 307 — which preserves method and body,
    /// unlike a 302 — must end the exchange rather than re-send it to whatever host the Location
    /// names. The token URL is an environment override, so an attacker who can set one variable is
    /// exactly who this stops.
    /// </summary>
    [Test]
    public async Task A_redirect_from_the_token_endpoint_does_not_re_send_the_credential() {
        _server.Given(Request.Create().WithPath("/oauth2/token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(307)
                .WithHeader("Location", $"{_server.Urls[0]}/harvested"));

        _server.Given(Request.Create().WithPath("/harvested").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"access_token":"tok_harvested","expires_in":3600}"""));

        var result = await _sp.GetRequiredService<WorkOSClient>().MintAsync(
            new MachineCredential("client_01ABC", "sekrit"),
            $"{_server.Urls[0]}/oauth2/token",
            CancellationToken.None);

        await Assert.That(result.Token).IsNull();
        await Assert.That(_server.LogEntries.Any(e => e.RequestMessage.Path == "/harvested")).IsFalse()
            .Because("the secret is in the body — a hop would hand it to the host the 3xx names");
    }

    /// <summary>A mint reports the endpoint's status without ever quoting the body it came with.</summary>
    [Test]
    public async Task A_rejected_mint_reports_the_status_and_not_the_body() {
        _server.Given(Request.Create().WithPath("/oauth2/token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(403)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"echo":"client_secret=sekrit"}"""));

        var result = await _sp.GetRequiredService<WorkOSClient>().MintAsync(
            new MachineCredential("client_01ABC", "sekrit"),
            $"{_server.Urls[0]}/oauth2/token",
            CancellationToken.None);

        await Assert.That(result.Token).IsNull();
        await Assert.That(result.Problem!).Contains("403");
        await Assert.That(result.Problem!).DoesNotContain("sekrit");
    }
}
