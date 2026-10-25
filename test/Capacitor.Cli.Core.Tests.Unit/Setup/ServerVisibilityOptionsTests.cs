using Capacitor.Cli.Core.Setup;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.Setup;

/// <summary>The visibility-options read hands back the stops this build knows, says nothing for a server that
/// answers 404 for the route, and turns every other miss into a reason rather than a throw</summary>
public class ServerVisibilityOptionsTests {
    const string ThreeStops = """
        {"options":[{"key":"private","label":"Only me","detail":"Nobody else."},
                    {"key":"project","label":"Project members","detail":"Members of your projects."},
                    {"key":"public","label":"Everyone","detail":"Every member."}],
         "default_visibility":"project"}
        """;

    [Test]
    [Arguments(200, ThreeStops, "private,project,public", "project", false)]
    [Arguments(200, """{"options":[{"key":"team_only","label":"Team"},{"key":"private","label":"Only me"}],"default_visibility":"private"}""", "private", "private", false)]
    [Arguments(200, """{"options":[{"key":"private","label":"Only me"}],"default_visibility":"team_only"}""", null, null, true)]
    [Arguments(200, "not json", null, null, true)]
    [Arguments(404, "", null, null, false)]
    [Arguments(500, "", null, null, true)]
    [Arguments(401, "", null, null, true)]
    public async Task Read_offers_the_known_stops_or_says_why_it_cannot(
            int status, string body, string? keys, string? preselected, bool problem) {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/users/me/visibility-options").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body));

        using var http = new HttpClient();
        var read = await ServerVisibilityOptions.ReadAsync(http, server.Url! + "/", CancellationToken.None);

        await Assert.That(read.Offer is null ? null : string.Join(",", read.Offer.Options.Select(o => o.Key))).IsEqualTo(keys);
        await Assert.That(read.Offer?.DefaultVisibility).IsEqualTo(preselected);
        await Assert.That(read.Problem is not null).IsEqualTo(problem);
    }

    sealed class Failing : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("handler broke");
    }

    [Test]
    public async Task Any_failure_short_of_cancellation_is_a_reason_not_a_throw() {
        using var http = new HttpClient(new Failing());
        var read = await ServerVisibilityOptions.ReadAsync(http, "http://kcap.test", CancellationToken.None);

        await Assert.That(read.Offer).IsNull();
        await Assert.That(read.Problem).IsEqualTo("handler broke");
    }

    [Test]
    public async Task An_unreachable_server_is_a_reason_not_a_throw() {
        using var http = new HttpClient();
        var read = await ServerVisibilityOptions.ReadAsync(http, "http://127.0.0.1:9", CancellationToken.None);

        await Assert.That(read.Offer).IsNull();
        await Assert.That(read.Problem).IsNotNull();
    }
}
