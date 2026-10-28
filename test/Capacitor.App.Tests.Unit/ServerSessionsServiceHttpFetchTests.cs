using Capacitor.App.Services;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.App.Tests.Unit;

public class ServerSessionsServiceHttpFetchTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task Fetch_reads_the_listing_and_maps_401_to_unauthorized() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/listing").UsingGet()
                  .WithParam("state", "ended").WithParam("limit", "20").WithParam("offset", "0").WithParam("owner", "me"))
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                  .WithBody("""{"items":[{"session_id":"s1","status":"ended","access_level":"full","started_at":"2026-10-22T13:04:24Z","last_activity_at":"2026-10-22T16:56:41Z"}],"total":212,"limit":20,"offset":0}"""));
        var (http, profiles, provider) = await WireMockLane.BuildAsync(server, Config.Root);
        await using var _ = provider;

        var fetch = ServerSessionsService.HttpFetch(http, profiles);
        var ok = await fetch(new ServerSessionsQuery("ended", "me", 20, 0), CancellationToken.None);
        await Assert.That(ok.Rows![0].SessionId).IsEqualTo("s1");
        await Assert.That(ok.Total).IsEqualTo(212);
        await Assert.That(ok.Unauthorized).IsFalse();

        server.Reset();
        server.Given(Request.Create().WithPath("/api/sessions/listing").UsingGet()).RespondWith(Response.Create().WithStatusCode(401));
        var denied = await fetch(new ServerSessionsQuery("ended", "me", 20, 0), CancellationToken.None);
        await Assert.That(denied.Rows).IsNull();
        await Assert.That(denied.Unauthorized).IsTrue();
    }

    [Test]
    public async Task Fetch_maps_a_refused_read_to_the_servers_retry_time() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/listing").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(429).WithHeader("Retry-After", "12").WithHeader("Content-Type", "application/json")
                  .WithBody("""{"error":"read_rate_limited","retry_after_seconds":12}"""));
        var (http, profiles, provider) = await WireMockLane.BuildAsync(server, Config.Root);
        await using var _ = provider;

        var refused = await ServerSessionsService.HttpFetch(http, profiles)(new ServerSessionsQuery("active", null, 100, 0), CancellationToken.None);
        await Assert.That(refused.Rows).IsNull();
        await Assert.That(refused.Unauthorized).IsFalse();
        await Assert.That(refused.RetryAfter).IsEqualTo(TimeSpan.FromSeconds(12));
    }

    [Test]
    public async Task Owners_fetch_reads_the_owners_route() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/owners").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                  .WithBody("""[{"user_id":"user_01","username":"alexey","display_name":"Alexey","avatar_url":null,"is_me":true,"ids":["user_01"]}]"""));
        var (http, profiles, provider) = await WireMockLane.BuildAsync(server, Config.Root);
        await using var _ = provider;

        var owners = await ServerSessionsService.HttpOwners(http, profiles)(CancellationToken.None);
        await Assert.That(owners![0].IsMe).IsTrue();
    }
}
