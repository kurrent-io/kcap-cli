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
    /// A 307 answered before the token exists is followed, and the mint still succeeds. The endpoint
    /// is an operator override defaulting to our own sign-in host, so a scheme or path normalisation
    /// in front of it is the deployment's business, not a rejection of the credential. A 307 preserves
    /// method and body, which is what makes the second leg a mint rather than a bare GET.
    /// </summary>
    [Test]
    public async Task A_redirect_before_the_token_is_followed_and_still_mints() {
        _server.Given(Request.Create().WithPath("/oauth2/token").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(307)
                .WithHeader("Location", $"{_server.Urls[0]}/oauth2/token/regional"));

        _server.Given(Request.Create().WithPath("/oauth2/token/regional").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"access_token":"tok_after_hop","expires_in":3600}"""));

        var result = await _sp.GetRequiredService<WorkOSClient>().MintAsync(
            new MachineCredential("client_01ABC", "sekrit"),
            $"{_server.Urls[0]}/oauth2/token",
            CancellationToken.None);

        await Assert.That(result.Token).IsEqualTo("tok_after_hop");
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

    /// <summary>A refresh that WorkOS honours rotates and carries the parsed tokens back to the caller.</summary>
    [Test]
    public async Task A_honoured_refresh_rotates_and_carries_the_response() {
        _server.Given(Request.Create().WithPath("/user_management/authenticate").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                """{"user":{"id":"user_x"},"access_token":"acc","refresh_token":"rt2"}"""));

        var result = await new WorkOSClient(new PlainHttpClientFactory(new StubHost(_server.Urls[0])))
            .RefreshAsync("client_d", "rt1", CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(WorkOSRefreshOutcome.Rotated);
        await Assert.That(result.Response!.AccessToken).IsEqualTo("acc");
        await Assert.That(result.Response!.RefreshToken).IsEqualTo("rt2");
    }

    /// <summary>
    /// A refused refresh reads as Rejected with no response — the token WorkOS declined must not be
    /// mistaken for a live one — and the refresh is sent once, never retried onto a consumed token.
    /// </summary>
    [Test]
    public async Task A_refused_refresh_is_rejected_and_sent_exactly_once() {
        _server.Given(Request.Create().WithPath("/user_management/authenticate").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400).WithBody(
                """{"error":"invalid_grant"}"""));

        var result = await new WorkOSClient(new PlainHttpClientFactory(new StubHost(_server.Urls[0])))
            .RefreshAsync("client_d", "rt1", CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(WorkOSRefreshOutcome.Rejected);
        await Assert.That(result.Response).IsNull();

        var posts = _server.FindLogEntries(
            Request.Create().WithPath("/user_management/authenticate").UsingPost());
        await Assert.That(posts.Count).IsEqualTo(1);
    }

    /// <summary>A 5xx is the server faltering, not the token being refused: it must read as a
    /// transport failure (retry with the same live token), not Rejected (which would strand the
    /// daemon on the hour-long re-login backoff over a transient blip).</summary>
    [Test]
    public async Task A_server_error_is_a_transport_failure_not_a_rejection() {
        _server.Given(Request.Create().WithPath("/user_management/authenticate").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(503));

        var result = await new WorkOSClient(new PlainHttpClientFactory(new StubHost(_server.Urls[0])))
            .RefreshAsync("client_d", "rt1", CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(WorkOSRefreshOutcome.TransportFailed);
        await Assert.That(result.Response).IsNull();
    }
}
