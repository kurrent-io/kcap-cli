using Capacitor.App.Services;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.App.Tests.Unit;

public class RemoteAgentsServiceHttpFetchTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task Fetch_reads_agent_instances_and_maps_401_to_unauthorized() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/agent-instances").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                  .WithBody("""[{"agent_id":"a1","status":"Running","daemon_name":"work-mac","vendor":"claude","registered_at":"2026-09-10T10:00:00Z"}]"""));
        var (http, profiles, provider) = await WireMockLane.BuildAsync(server, Config.Root);
        await using var _ = provider;

        var fetch = RemoteAgentsService.HttpFetch(http, profiles);
        var ok = await fetch(CancellationToken.None);
        await Assert.That(ok.Rows![0].AgentId).IsEqualTo("a1");
        await Assert.That(ok.Unauthorized).IsFalse();

        server.Reset();
        server.Given(Request.Create().WithPath("/api/agent-instances").UsingGet()).RespondWith(Response.Create().WithStatusCode(401));
        var denied = await fetch(CancellationToken.None);
        await Assert.That(denied.Rows).IsNull();
        await Assert.That(denied.Unauthorized).IsTrue();
    }
}
