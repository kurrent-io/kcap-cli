using System.Diagnostics;
using System.Net;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Http;
using Microsoft.Extensions.DependencyInjection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using static Capacitor.Tests.Helpers.SequencedHttpScript;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// <summary>
/// The WorkOS lane as the container actually configures it. A client built by hand follows redirects,
/// so nothing short of resolving the registered one proves what the registration contributes.
/// </summary>
public class WorkOSClientTests : IDisposable {
    const string Rotation = """{"user":{"id":"user_x"},"access_token":"acc","refresh_token":"rt2"}""";

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
        var workos = new SequencedHttpScript(Reply(HttpStatusCode.OK, Rotation));

        var result = await new WorkOSClient(new PlainHttpClientFactory(workos))
            .RefreshAsync("client_d", "rt1", CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(WorkOSRefreshOutcome.Rotated);
        await Assert.That(result.Response!.AccessToken).IsEqualTo("acc");
        await Assert.That(result.Response!.RefreshToken).IsEqualTo("rt2");
        await Assert.That(workos.Count).IsEqualTo(1);
    }

    /// <summary>
    /// A refused refresh reads as Rejected with no response — the token WorkOS declined must not be
    /// mistaken for a live one — and a 4xx is never replayed: WorkOS understood the token and said no.
    /// </summary>
    [Test]
    public async Task A_refused_refresh_is_rejected_and_sent_exactly_once() {
        var workos = new SequencedHttpScript(Reply(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""));

        var result = await new WorkOSClient(new PlainHttpClientFactory(workos))
            .RefreshAsync("client_d", "rt1", CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(WorkOSRefreshOutcome.Rejected);
        await Assert.That(result.Response).IsNull();
        await Assert.That(workos.Count).IsEqualTo(1);
    }

    /// <summary>
    /// A refresh whose reply never arrives is replayed with the same token, and the replay's rotated
    /// pair is the result. WorkOS may already have processed the first exchange, so only a replay
    /// inside its window can recover the successor; a later fresh refresh cannot.
    /// </summary>
    [Test]
    public async Task A_timed_out_refresh_is_replayed_inside_the_grace_window() {
        var workos = new SequencedHttpScript(Stall(), Reply(HttpStatusCode.OK, Rotation));

        var result = await Client(workos).RefreshAsync("client_d", "rt1", CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(WorkOSRefreshOutcome.Rotated);
        await Assert.That(result.Response!.RefreshToken).IsEqualTo("rt2");
        await Assert.That(workos.Count).IsEqualTo(2);
        await Assert.That(workos.Bodies.Distinct().Count()).IsEqualTo(1);
    }

    /// <summary>A 5xx is the server faltering, not the token being refused: the same token is replayed
    /// and the replay rotates.</summary>
    [Test]
    public async Task A_server_error_is_replayed_with_the_same_token() {
        var workos = new SequencedHttpScript(
            Reply(HttpStatusCode.ServiceUnavailable), Reply(HttpStatusCode.OK, Rotation));

        var result = await Client(workos).RefreshAsync("client_d", "rt1", CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(WorkOSRefreshOutcome.Rotated);
        await Assert.That(workos.Count).IsEqualTo(2);
        await Assert.That(workos.Bodies.Distinct().Count()).IsEqualTo(1);
    }

    /// <summary>Within the grace window a replay returns the same rotated tokens, so an unreadable
    /// success body is not the end of the session — the replay recovers what the first reply lost.</summary>
    [Test]
    public async Task An_unreadable_success_body_is_replayed_and_the_replay_rotates() {
        var workos = new SequencedHttpScript(Reply(HttpStatusCode.OK, "not-json"), Reply(HttpStatusCode.OK, Rotation));

        var result = await Client(workos).RefreshAsync("client_d", "rt1", CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(WorkOSRefreshOutcome.Rotated);
        await Assert.That(workos.Count).IsEqualTo(2);
    }

    /// <summary>Replays stop once the next attempt could no longer land inside the grace window; a
    /// WorkOS that never answers reads as a transport failure, and the caller is not held past the
    /// budget.</summary>
    [Test]
    public async Task Replays_stop_at_the_grace_budget_and_a_dead_endpoint_is_a_transport_failure() {
        var workos = new SequencedHttpScript(Stall());

        var started = Stopwatch.GetTimestamp();
        var result  = await Client(workos).RefreshAsync("client_d", "rt1", CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(WorkOSRefreshOutcome.TransportFailed);
        await Assert.That(workos.Count).IsGreaterThan(1);
        await Assert.That(Stopwatch.GetElapsedTime(started)).IsLessThan(TimeSpan.FromSeconds(8));
    }

    /// <summary>A success whose body is never readable has consumed the token and lost its successor
    /// — once the replays run out, that is Rejected, never a retryable failure.</summary>
    [Test]
    public async Task A_persistently_unreadable_success_is_rejected_once_replays_run_out() {
        var workos = new SequencedHttpScript(Reply(HttpStatusCode.OK, "not-json"));

        var result = await Client(workos).RefreshAsync("client_d", "rt1", CancellationToken.None);

        await Assert.That(result.Outcome).IsEqualTo(WorkOSRefreshOutcome.Rejected);
        await Assert.That(result.Response).IsNull();
        await Assert.That(workos.Count).IsGreaterThan(1);
    }

    /// <summary>The caller's own cancellation is never swallowed into a transport failure or a replay.</summary>
    [Test]
    public async Task The_callers_cancellation_propagates_instead_of_being_replayed() {
        var workos = new SequencedHttpScript(Stall());

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var client = new WorkOSClient(
            new PlainHttpClientFactory(workos),
            refreshTimeout: TimeSpan.FromSeconds(10),
            replayBudget:   TimeSpan.FromSeconds(60),
            replayBackoff:  TimeSpan.FromSeconds(1));

        await Assert.That(async () => await client.RefreshAsync("client_d", "rt1", cts.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(workos.Count).IsEqualTo(1);
    }

    // Deadlines short enough that a stalled script runs the loop out in a few seconds, yet long
    // enough that a cold HttpClient's first send under a fully parallel suite lands inside them.
    static WorkOSClient Client(SequencedHttpScript workos) => new(
        new PlainHttpClientFactory(workos),
        refreshTimeout: TimeSpan.FromSeconds(2),
        replayBudget:   TimeSpan.FromSeconds(5),
        replayBackoff:  TimeSpan.FromMilliseconds(10));
}
