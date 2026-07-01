using System.Net.Http;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Auth;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit;

public class TenantProvisioningClientTests {
    [Test]
    public async Task ProvisionAsync_sends_bearer_and_camelCase_body_and_parses_202() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/signup/provision").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(202)
                .WithBody("""{"slug":"acme","state":"provisioning"}""")
                .WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();
        var client = new TenantProvisioningClient(http);

        var outcome = await client.ProvisionAsync(server.Urls[0], "tok", "Acme Inc", "acme", CancellationToken.None);

        await Assert.That(outcome.StatusCode).IsEqualTo(202);
        await Assert.That(outcome.Body!.State).IsEqualTo("provisioning");

        var log = server.FindLogEntries(Request.Create().WithPath("/api/signup/provision").UsingPost());
        await Assert.That(log.Count).IsEqualTo(1);
        var req = log[0].RequestMessage;
        await Assert.That(req.Headers!["Authorization"][0]).IsEqualTo("Bearer tok");
        var body = JsonNode.Parse(req.Body!)!;
        await Assert.That(body["orgName"]!.GetValue<string>()).IsEqualTo("Acme Inc");
        await Assert.That(body["slug"]!.GetValue<string>()).IsEqualTo("acme");
        await Assert.That(body["tier"]!.GetValue<string>()).IsEqualTo("free");
    }

    [Test]
    public async Task ProvisionAsync_parses_409_reason() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/signup/provision").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(409)
                .WithBody("""{"reason":"taken"}""").WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();
        var client = new TenantProvisioningClient(http);

        var outcome = await client.ProvisionAsync(server.Urls[0], "tok", "Acme", "acme", CancellationToken.None);
        await Assert.That(outcome.StatusCode).IsEqualTo(409);
        await Assert.That(outcome.Body!.Reason).IsEqualTo("taken");
    }

    [Test]
    public async Task CheckAvailabilityAsync_parses_reason() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/signup/availability").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody("""{"available":false,"reason":"reserved"}""").WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();
        var client = new TenantProvisioningClient(http);

        var avail = await client.CheckAvailabilityAsync(server.Urls[0], "tok", "acme", CancellationToken.None);
        await Assert.That(avail!.Available).IsFalse();
        await Assert.That(avail.Reason).IsEqualTo("reserved");
    }

    [Test]
    public async Task GetStatusAsync_parses_active_with_workosOrgId() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/signup/status").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200)
                .WithBody("""{"state":"active","url":"https://acme.kcap.ai","workosOrgId":"org_live"}""")
                .WithHeader("Content-Type", "application/json"));

        using var http = new HttpClient();
        var client = new TenantProvisioningClient(http);

        var status = await client.GetStatusAsync(server.Urls[0], "tok", "acme", CancellationToken.None);
        await Assert.That(status!.State).IsEqualTo("active");
        await Assert.That(status.WorkosOrgId).IsEqualTo("org_live");
    }
}
