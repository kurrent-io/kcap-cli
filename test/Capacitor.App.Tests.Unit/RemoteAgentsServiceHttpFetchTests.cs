using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
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

    [Test]
    public async Task HttpRename_posts_rename_true() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/hooks/agent-title").UsingPost()
                  .WithBody(b => b!.Contains("\"rename\":true") && b.Contains("\"session_id\":\"s1\"") && b.Contains("\"title\":\"New name\"")))
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                  .WithBody("""{"applied":true,"title":"New name"}"""));
        var (http, profiles, provider) = await WireMockLane.BuildAsync(server, Config.Root);
        await using var _ = provider;

        var result = await RemoteAgentsService.HttpRename(http, profiles)("s1", "  New name ", CancellationToken.None);

        await Assert.That(result).IsEqualTo(new SessionRenameResult(true, "New name", null));
    }

    [Test]
    [Arguments(200, """{"applied":false,"title":"Kept"}""", false, "Kept", SessionRenameReasons.Refused)]
    [Arguments(403, "", false, null, SessionRenameReasons.NotOwned)]
    [Arguments(404, "", false, null, SessionRenameReasons.NotFound)]
    [Arguments(401, "", false, null, SessionRenameReasons.Unauthenticated)]
    [Arguments(500, "", false, null, SessionRenameReasons.ServerError)]
    [Arguments(200, "not json", false, null, SessionRenameReasons.ServerError)]
    public async Task HttpRename_maps_the_status(int status, string body, bool applied, string? title, string reason) {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/hooks/agent-title").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body));
        var (http, profiles, provider) = await WireMockLane.BuildAsync(server, Config.Root);
        await using var _ = provider;

        var result = await RemoteAgentsService.HttpRename(http, profiles)("s1", "New", CancellationToken.None);

        await Assert.That(result).IsEqualTo(new SessionRenameResult(applied, title, reason));
    }

    [Test]
    public async Task HttpRename_without_a_server_is_unauthenticated_and_a_blank_title_is_malformed() {
        var none = await RemoteAgentsService.HttpRename(null, null)("s1", "New", CancellationToken.None);
        await Assert.That(none.Reason).IsEqualTo(SessionRenameReasons.Unauthenticated);

        using var server = WireMockServer.Start();
        var (http, profiles, provider) = await WireMockLane.BuildAsync(server, Config.Root);
        await using var _ = provider;
        var blank = await RemoteAgentsService.HttpRename(http, profiles)("s1", "   ", CancellationToken.None);
        await Assert.That(blank.Reason).IsEqualTo(SessionRenameReasons.Malformed);
        await Assert.That(server.LogEntries).IsEmpty();
    }
}
