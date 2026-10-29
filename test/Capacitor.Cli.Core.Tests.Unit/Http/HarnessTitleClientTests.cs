using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Http;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.Http;

public class HarnessTitleClientTests {
    /// <summary>Real timers, but a monotonic clock that can be jumped forward — so the time a request took can be
    /// made to look long without waiting for it.</summary>
    sealed class JumpableClock : TimeProvider {
        long _offsetTicks;

        public void Jump(TimeSpan by) => Interlocked.Add(ref _offsetTicks, (long)(by.TotalSeconds * TimestampFrequency));

        public override long GetTimestamp() => System.GetTimestamp() + Interlocked.Read(ref _offsetTicks);
    }

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

        await Assert.That(outcome).IsEqualTo(HarnessTitleOutcome.PostedToLegacyRoute);
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

    [Test]
    [Arguments(401)]
    [Arguments(408)]
    [Arguments(429)]
    [Arguments(503)]
    public async Task Transient_statuses_are_failed_not_refused(int status) {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/hooks/harness-title").UsingPost()).RespondWith(Response.Create().WithStatusCode(status));
        using var client = new HttpClient();

        var outcome = await HarnessTitleClient.PostAsync(client, TimeProvider.System, server.Url!, "abc",
            new HarnessTitlePost("Name", HarnessTitleKind.Auto, null), default);

        await Assert.That(outcome).IsEqualTo(HarnessTitleOutcome.Failed);
    }

    [Test]
    public async Task Status_422_is_refused() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/hooks/harness-title").UsingPost()).RespondWith(Response.Create().WithStatusCode(422));
        using var client = new HttpClient();

        var outcome = await HarnessTitleClient.PostAsync(client, TimeProvider.System, server.Url!, "abc",
            new HarnessTitlePost("Name", HarnessTitleKind.Auto, null), default);

        await Assert.That(outcome).IsEqualTo(HarnessTitleOutcome.Refused);
    }

    [Test]
    public async Task Fallback_server_fault_is_failed() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/hooks/set-title").UsingPost()).RespondWith(Response.Create().WithStatusCode(500));
        using var client = new HttpClient();

        var outcome = await HarnessTitleClient.PostOrFallBackAsync(client, TimeProvider.System, server.Url!, "abc",
            new HarnessTitlePost("Name", HarnessTitleKind.Auto, null), default);

        await Assert.That(outcome).IsEqualTo(HarnessTitleOutcome.Failed);
    }

    [Test]
    public async Task Fallback_403_is_refused() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/hooks/set-title").UsingPost()).RespondWith(Response.Create().WithStatusCode(403));
        using var client = new HttpClient();

        var outcome = await HarnessTitleClient.PostOrFallBackAsync(client, TimeProvider.System, server.Url!, "abc",
            new HarnessTitlePost("Name", HarnessTitleKind.Auto, null), default);

        await Assert.That(outcome).IsEqualTo(HarnessTitleOutcome.Refused);
    }

    [Test]
    public async Task Fallback_clamps_title_to_120() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/hooks/set-title").UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        using var client = new HttpClient();

        await HarnessTitleClient.PostOrFallBackAsync(client, TimeProvider.System, server.Url!, "abc",
            new HarnessTitlePost(new string('k', 500), HarnessTitleKind.Auto, null), default);

        var body = JsonNode.Parse(server.LogEntries.Single(e => e.RequestMessage.Path == "/hooks/set-title").RequestMessage.Body!)!;
        await Assert.That(body["title"]!.GetValue<string>()).IsEqualTo(new string('k', 120));
    }

    [Test]
    public async Task Harness_title_body_is_sent_whole_for_the_server_to_normalise_and_clamp() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/hooks/harness-title").UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        using var client = new HttpClient();
        var title = string.Join("  ", Enumerable.Repeat("word", 100));

        await HarnessTitleClient.PostAsync(client, TimeProvider.System, server.Url!, "abc",
            new HarnessTitlePost(title, HarnessTitleKind.Rename, null), default);

        var body = JsonNode.Parse(server.LogEntries.Single().RequestMessage.Body!)!;
        await Assert.That(body["title"]!.GetValue<string>()).IsEqualTo(title);
    }

    [Test]
    [Arguments("[]")]
    [Arguments("\"x\"")]
    [Arguments("not json")]
    public async Task A_404_body_that_is_not_an_error_object_is_route_missing(string body) {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/hooks/harness-title").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(404).WithBody(body));
        using var client = new HttpClient();

        var outcome = await HarnessTitleClient.PostAsync(client, TimeProvider.System, server.Url!, "abc",
            new HarnessTitlePost("Name", HarnessTitleKind.Auto, null), default);

        await Assert.That(outcome).IsEqualTo(HarnessTitleOutcome.RouteMissing);
    }

    [Test]
    public async Task A_server_slower_than_the_budget_returns_within_it() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/hooks/harness-title").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(20)));
        using var client = new HttpClient();

        var started = TimeProvider.System.GetTimestamp();
        var outcome = await HarnessTitleClient.PostOrFallBackAsync(client, TimeProvider.System, server.Url!, "abc",
            new HarnessTitlePost("Name", HarnessTitleKind.Auto, null), default, TimeSpan.FromSeconds(1));

        await Assert.That(outcome).IsEqualTo(HarnessTitleOutcome.Failed);
        await Assert.That(TimeProvider.System.GetElapsedTime(started)).IsLessThan(TimeSpan.FromSeconds(10));
    }

    /// <summary>The fallback gets what is left of the one budget, and an omitted budget is no exception: with 31s
    /// already spent against the 30s default, the set-title request is not sent at all.</summary>
    [Test]
    public async Task An_omitted_budget_is_shared_with_the_fallback_not_restarted() {
        using var server = WireMockServer.Start();
        var clock = new JumpableClock();
        server.Given(Request.Create().WithPath("/hooks/harness-title").UsingPost())
            .RespondWith(Response.Create().WithCallback(_ => {
                clock.Jump(TimeSpan.FromSeconds(31));
                return new WireMock.ResponseMessage { StatusCode = 404 };
            }));
        server.Given(Request.Create().WithPath("/hooks/set-title").UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        using var client = new HttpClient();

        var outcome = await HarnessTitleClient.PostOrFallBackAsync(client, clock, server.Url!, "abc",
            new HarnessTitlePost("Name", HarnessTitleKind.Auto, null), default);

        await Assert.That(outcome).IsEqualTo(HarnessTitleOutcome.Failed);
        await Assert.That(server.LogEntries.Any(e => e.RequestMessage.Path == "/hooks/set-title")).IsFalse();
    }

    [Test]
    public async Task A_transient_status_is_retried_when_the_caller_asks() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/hooks/harness-title").UsingPost()).InScenario("blip").WillSetStateTo("ok")
            .RespondWith(Response.Create().WithStatusCode(503));
        server.Given(Request.Create().WithPath("/hooks/harness-title").UsingPost()).InScenario("blip").WhenStateIs("ok")
            .RespondWith(Response.Create().WithStatusCode(200));
        using var client = new HttpClient();

        var outcome = await HarnessTitleClient.PostOrFallBackAsync(client, TimeProvider.System, server.Url!, "abc",
            new HarnessTitlePost("Name", HarnessTitleKind.Auto, null), default, retryStatuses: true);

        await Assert.That(outcome).IsEqualTo(HarnessTitleOutcome.Posted);
        await Assert.That(server.LogEntries.Count(e => e.RequestMessage.Path == "/hooks/harness-title")).IsEqualTo(2);
    }

    [Test]
    [Arguments(400, """{"error":"unsafe_session_id"}""", true)]
    [Arguments(400, "", true)]
    [Arguments(404, "", false)]
    [Arguments(404, """{"error":"session_not_found"}""", true)]
    [Arguments(401, "", null)]
    [Arguments(503, "", null)]
    public async Task The_probe_tells_a_server_with_harness_titles_from_one_without(int status, string body, bool? expected) {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/hooks/harness-title").UsingPost()).RespondWith(Response.Create().WithStatusCode(status).WithBody(body));
        using var client = new HttpClient();

        var supported = await HarnessTitleClient.ServerRecordsHarnessTitlesAsync(client, TimeProvider.System, server.Url!, TimeSpan.FromSeconds(10), default);

        await Assert.That(supported).IsEqualTo(expected);
        var sent = JsonNode.Parse(server.LogEntries.Single().RequestMessage.Body!)!;
        await Assert.That(sent["session_id"]!.GetValue<string>()).IsEqualTo("");
    }

    [Test]
    public async Task The_probe_answers_unknown_when_the_server_is_unreachable() {
        using var client = new HttpClient();

        var supported = await HarnessTitleClient.ServerRecordsHarnessTitlesAsync(client, TimeProvider.System, "http://127.0.0.1:1", TimeSpan.FromSeconds(1), default);

        await Assert.That(supported).IsNull();
    }
}
