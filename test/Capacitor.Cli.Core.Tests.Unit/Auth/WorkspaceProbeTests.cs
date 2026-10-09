using Capacitor.Cli.Core.Auth;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// <summary>Only a 404 reads as a missing workspace; anything else that is not a success is an outage.</summary>
public class WorkspaceProbeTests {
    [Test]
    [Arguments(200, WorkspaceAnswer.Live)]
    [Arguments(404, WorkspaceAnswer.Gone)]
    [Arguments(500, WorkspaceAnswer.NoAnswer)]
    [Arguments(502, WorkspaceAnswer.NoAnswer)]
    public async Task Reads_the_auth_config_status(int status, WorkspaceAnswer expected) {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(status).WithBody("""{"provider":"WorkOS"}"""));
        using var client = new HttpClient();

        var answer = await WorkspaceProbe.AskAsync(client, server.Urls[0] + "/", TimeProvider.System);

        await Assert.That(answer).IsEqualTo(expected);
    }

    [Test]
    public async Task A_refused_connection_is_no_answer() {
        using var server = WireMockServer.Start();
        var       url    = server.Urls[0];
        server.Stop();
        using var client = new HttpClient();

        await Assert.That(await WorkspaceProbe.AskAsync(client, url, TimeProvider.System)).IsEqualTo(WorkspaceAnswer.NoAnswer);
    }
}
