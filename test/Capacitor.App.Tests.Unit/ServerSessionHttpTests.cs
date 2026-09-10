using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Remote.Models;
using Microsoft.Extensions.DependencyInjection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.App.Tests.Unit;

/// Builds the app's real authenticated HTTP lane against a WireMock server.
static class WireMockLane {
    public static async Task<(ICapacitorHttpClient Http, ProfileContext Profiles, ServiceProvider Provider)> BuildAsync(WireMockServer server, ConfigRoot root) {
        var profiles = Resolutions.At(server.Url!, root);
        await AuthFixtures.NewTokenStore(root).SaveAsync(profiles.Name, new StoredTokens {
            AccessToken = "tok", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), GitHubUsername = "alice",
            Provider = AuthProvider.GitHubApp, ServerUrl = server.Url!,
        });
        var provider = new ServiceCollection()
            .AddSingleton(root).AddSingleton(profiles).AddSingleton(new CapacitorServer(server.Url!, root, profiles))
            .AddCapacitorHttp().BuildValidated();
        return (provider.GetRequiredService<ICapacitorHttpClient>(), profiles, provider);
    }
}

public class ServerSessionHttpTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task Responder_posts_the_payload_and_maps_status_codes() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/s1/permission-response/ok").UsingPost().WithBody(b => b!.Contains("\"behavior\":\"allow\"")))
              .RespondWith(Response.Create().WithStatusCode(200));
        server.Given(Request.Create().WithPath("/api/sessions/s1/permission-response/gone").UsingPost()).RespondWith(Response.Create().WithStatusCode(404));
        server.Given(Request.Create().WithPath("/api/sessions/s1/permission-response/bad").UsingPost()).RespondWith(Response.Create().WithStatusCode(400).WithBody("""{"error":"count mismatch"}"""));
        var (http, profiles, provider) = await WireMockLane.BuildAsync(server, Config.Root);
        await using var _ = provider;
        var respond = ServerSessionHttp.Responder(http, profiles);
        var allow = new PermissionResponsePayload { Behavior = PermissionBehaviors.Allow };

        await Assert.That((await respond("s1", "ok", allow, CancellationToken.None)).Kind).IsEqualTo(ServerRespondKind.Applied);
        await Assert.That((await respond("s1", "gone", allow, CancellationToken.None)).Kind).IsEqualTo(ServerRespondKind.NotPending);
        var rejected = await respond("s1", "bad", allow, CancellationToken.None);
        await Assert.That(rejected.Kind).IsEqualTo(ServerRespondKind.Rejected);
        await Assert.That(rejected.Reason).Contains("count mismatch");
    }

    [Test]
    public async Task Responder_without_a_server_or_client_is_unreachable_not_a_throw() {
        var outcome = await ServerSessionHttp.Responder(null, null)("s1", "r1", new PermissionResponsePayload { Behavior = "allow" }, CancellationToken.None);
        await Assert.That(outcome.Kind).IsEqualTo(ServerRespondKind.Unreachable);
    }

    [Test]
    public async Task Detail_reader_returns_the_parsed_detail_and_distinguishes_not_found() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/s1/detail").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                  .WithBody("""{"session_id":"s1","ended_at":null,"last_event_number":1,"events":[{"event_type":"InterruptIssued","event_number":1,"payload":{"request_id":"p1","kind":"permission","tool_name":"Bash"}}]}"""));
        server.Given(Request.Create().WithPath("/api/sessions/nope/detail").UsingGet()).RespondWith(Response.Create().WithStatusCode(404));
        var (http, profiles, provider) = await WireMockLane.BuildAsync(server, Config.Root);
        await using var _ = provider;
        var read = ServerSessionHttp.DetailReader(http, profiles);

        var found = await read("s1", CancellationToken.None);
        await Assert.That(found.Detail!.Events![0].EventType).IsEqualTo("InterruptIssued");
        var missing = await read("nope", CancellationToken.None);
        await Assert.That(missing.Detail).IsNull();
        await Assert.That(missing.NotFound).IsTrue();
    }
}
