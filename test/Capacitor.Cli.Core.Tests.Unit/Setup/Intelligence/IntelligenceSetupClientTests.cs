using Capacitor.Cli.Core.Setup.Intelligence;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.Setup.Intelligence;

/// <summary>The answer goes out snake_case; the server's answer comes back as applied, refused with its code, or
/// failed with a reason, never a throw</summary>
public class IntelligenceSetupClientTests {
    const string Path = "/api/admin/intelligence";

    [Test]
    public async Task The_request_is_snake_case() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(Path).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400).WithBody("""{"error":"x"}"""));

        using var http = new HttpClient();
        await IntelligenceSetupClient.ApplyAsync(http, server.Url!,
            new("provider", null, "anthropic", "m", "secret", ImportOutcomes: false), CancellationToken.None);

        var body = server.LogEntries.Single().RequestMessage.Body;
        await Assert.That(body).Contains("\"api_key\":\"secret\"");
        await Assert.That(body).Contains("\"import_outcomes\":false");
        await Assert.That(body).Contains("\"mode\":\"provider\"");
    }

    [Test]
    public async Task A_200_is_applied_with_the_budget_and_what_resolves() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(Path).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                """{"mode":"local_agent","harness":"claude","vendor":null,"import_outcomes":true,"backlog_session_touches_per_day":200,"resolving":["titles","outcomes"]}"""));

        using var http = new HttpClient();
        var outcome = await IntelligenceSetupClient.ApplyAsync(http, server.Url! + "/", new("local_agent", "claude"), CancellationToken.None);

        var applied = (IntelligenceSetupOutcome.Applied)outcome;
        await Assert.That(applied.Response.Harness).IsEqualTo("claude");
        await Assert.That(applied.Response.ImportOutcomes).IsTrue();
        await Assert.That(applied.Response.BacklogSessionTouchesPerDay).IsEqualTo(200);
        await Assert.That(applied.Response.ResolvesTitles).IsTrue();
    }

    [Test]
    public async Task A_400_is_refused_with_the_servers_code() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(Path).UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400).WithBody("""{"error":"key_rejected: 401 invalid x-api-key"}"""));

        using var http = new HttpClient();
        var outcome = await IntelligenceSetupClient.ApplyAsync(http, server.Url!, new("provider", null, "anthropic", null, "k"), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new IntelligenceSetupOutcome.Refused("key_rejected: 401 invalid x-api-key"));
    }

    [Test]
    [Arguments(404, "", "does not offer")]
    [Arguments(500, "boom", "HTTP 500")]
    [Arguments(400, "not json", "HTTP 400")]
    [Arguments(200, "not json", "unreadable")]
    public async Task Anything_else_fails_with_a_reason(int status, string body, string reason) {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(Path).UsingPost()).RespondWith(Response.Create().WithStatusCode(status).WithBody(body));

        using var http = new HttpClient();
        var outcome = await IntelligenceSetupClient.ApplyAsync(http, server.Url!, new("none"), CancellationToken.None);

        await Assert.That(((IntelligenceSetupOutcome.Failed)outcome).Problem).Contains(reason);
    }

    [Test]
    public async Task An_unreachable_server_fails_rather_than_throws() {
        using var http = new HttpClient();
        var outcome = await IntelligenceSetupClient.ApplyAsync(http, "http://127.0.0.1:9", new("none"), CancellationToken.None);

        await Assert.That(outcome).IsTypeOf<IntelligenceSetupOutcome.Failed>();
    }
}
