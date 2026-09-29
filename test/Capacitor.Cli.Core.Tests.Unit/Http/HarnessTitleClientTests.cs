using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Http;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.Http;

public class HarnessTitleClientTests {
    [Test]
    public async Task Posts_snake_case_body_with_kind_and_utc_changed_at() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/hooks/harness-title").UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        using var client = new HttpClient();

        var outcome = await HarnessTitleClient.PostAsync(client, TimeProvider.System, server.Url!, "abc",
            new HarnessTitlePost("Name", HarnessTitleKind.Rename, new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.FromHours(2))), default);

        await Assert.That(outcome).IsEqualTo(HarnessTitleOutcome.Posted);
        var body = JsonNode.Parse(server.LogEntries.Single().RequestMessage.Body!)!;
        await Assert.That(body["session_id"]!.GetValue<string>()).IsEqualTo("abc");
        await Assert.That(body["kind"]!.GetValue<string>()).IsEqualTo("rename");
        await Assert.That(body["changed_at"]!.GetValue<string>()).IsEqualTo("2026-09-29T10:00:00+00:00");
    }

    [Test]
    public async Task Omits_changed_at_when_unknown() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/hooks/harness-title").UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        using var client = new HttpClient();

        var outcome = await HarnessTitleClient.PostAsync(client, TimeProvider.System, server.Url!, "abc",
            new HarnessTitlePost("Name", HarnessTitleKind.Auto, null), default);

        await Assert.That(outcome).IsEqualTo(HarnessTitleOutcome.Posted);
        var body = JsonNode.Parse(server.LogEntries.Single().RequestMessage.Body!)!;
        await Assert.That(body.AsObject().ContainsKey("changed_at")).IsFalse();
    }

    [Test]
    public async Task Coded_404_is_session_not_found() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/hooks/harness-title").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(404).WithBody("""{"error":"session_not_found"}"""));
        using var client = new HttpClient();

        var outcome = await HarnessTitleClient.PostAsync(client, TimeProvider.System, server.Url!, "abc",
            new HarnessTitlePost("Name", HarnessTitleKind.Auto, null), default);

        await Assert.That(outcome).IsEqualTo(HarnessTitleOutcome.SessionNotFound);
    }

    [Test]
    public async Task Bare_404_is_route_missing_and_falls_back_to_set_title() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/hooks/set-title").UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        using var client = new HttpClient();

        var outcome = await HarnessTitleClient.PostOrFallBackAsync(client, TimeProvider.System, server.Url!, "abc",
            new HarnessTitlePost("Name", HarnessTitleKind.Auto, null), default);

        await Assert.That(outcome).IsEqualTo(HarnessTitleOutcome.Posted);
        var setTitleRequest = server.LogEntries.Single(e => e.RequestMessage.Path == "/hooks/set-title");
        var body            = JsonNode.Parse(setTitleRequest.RequestMessage.Body!)!;
        await Assert.That(body["session_id"]!.GetValue<string>()).IsEqualTo("abc");
        await Assert.That(body["title"]!.GetValue<string>()).IsEqualTo("Name");
    }

    [Test]
    public async Task Coded_404_does_not_fall_back() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/hooks/harness-title").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(404).WithBody("""{"error":"session_not_found"}"""));
        using var client = new HttpClient();

        var outcome = await HarnessTitleClient.PostOrFallBackAsync(client, TimeProvider.System, server.Url!, "abc",
            new HarnessTitlePost("Name", HarnessTitleKind.Auto, null), default);

        await Assert.That(outcome).IsEqualTo(HarnessTitleOutcome.SessionNotFound);
        await Assert.That(server.LogEntries.Any(e => e.RequestMessage.Path == "/hooks/set-title")).IsFalse();
    }

    [Test]
    public async Task Status_400_and_403_are_refused() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/hooks/harness-title").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400).WithBody("""{"error":"title_blank"}"""));
        using var client = new HttpClient();

        var badTitle = await HarnessTitleClient.PostAsync(client, TimeProvider.System, server.Url!, "abc",
            new HarnessTitlePost("", HarnessTitleKind.Auto, null), default);

        await Assert.That(badTitle).IsEqualTo(HarnessTitleOutcome.Refused);

        server.Reset();
        server.Given(Request.Create().WithPath("/hooks/harness-title").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(403));

        var notOwner = await HarnessTitleClient.PostAsync(client, TimeProvider.System, server.Url!, "abc",
            new HarnessTitlePost("Name", HarnessTitleKind.Auto, null), default);

        await Assert.That(notOwner).IsEqualTo(HarnessTitleOutcome.Refused);
    }
}
