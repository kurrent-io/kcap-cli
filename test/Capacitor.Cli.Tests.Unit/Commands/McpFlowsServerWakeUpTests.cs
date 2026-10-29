using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.PrDetection;
using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>The wake-up tools of <c>kcap mcp flows</c>, dispatched against a recording server: the route and body
/// each one sends, how a refusal reads, and what an agent is told when the server has no wake-ups.</summary>
public class McpFlowsServerWakeUpTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string WakeUpId = "2f1c9a8e-5b7d-4c3a-9e2f-0123456789ab";

    McpFlowsServer Server() =>
        new(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root),
            new FixedCapacitorHttpClient(), NoTelemetry.Startup, router: new GitProviderRouter(), workdir: new WorkingDirectory(AppContext.BaseDirectory), time: TimeProvider.System);

    sealed record Call(string Text, bool IsError, ILogEntry? Request);

    async Task<Call> CallAsync(string tool, string arguments, int status = 200, string body = "{}", string? agent = "agent-7", string? url = null) {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().UsingAnyMethod())
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body));

        using var client = new HttpClient();
        var request = new JsonObject {
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = JsonNode.Parse(arguments) }
        };

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, request, client, url ?? server.Url!, cwd: "/elsewhere", repoRoot: null, repoInfo: null,
            requestingSessionId: "s1", callerAgentId: agent);
        var result = JsonNode.Parse(response)!["result"]!;

        return new(result["content"]![0]!["text"]!.GetValue<string>(), result["isError"]?.GetValue<bool>() ?? false, server.LogEntries.SingleOrDefault());
    }

    static JsonNode SentBody(Call call) => JsonNode.Parse(call.Request!.RequestMessage.Body!)!;

    static string SentQuery(Call call) => (call.Request!.RequestMessage.RawQuery ?? "").TrimStart('?');

    [Test]
    public async Task Schedule_by_delay_posts_the_session_the_calling_agent_and_the_delay_in_seconds() {
        var call = await CallAsync("schedule_wakeup", """{"delay_minutes":20,"prompt":"Read the review findings."}""",
            body: $$"""{"wakeup_id":"{{WakeUpId}}","fire_at":"2026-10-24T15:20:00+00:00"}""");

        await Assert.That(call.IsError).IsFalse();
        await Assert.That(call.Text).Contains($"wakeup_id: {WakeUpId}");
        await Assert.That(call.Text).Contains("fire_at: 2026-10-24T15:20:00+00:00");
        await Assert.That(call.Text).Contains("End your turn");
        await Assert.That(call.Request!.RequestMessage.Method).IsEqualTo("POST");
        await Assert.That(call.Request.RequestMessage.Path).IsEqualTo("/api/wakeups");
        await Assert.That(JsonNode.DeepEquals(SentBody(call), JsonNode.Parse(
            """{"session_id":"s1","prompt":"Read the review findings.","caller_agent_id":"agent-7","delay_seconds":1200}"""))).IsTrue();
    }

    [Test]
    public async Task Schedule_at_a_time_posts_it_as_fire_at() {
        var call = await CallAsync("schedule_wakeup", """{"at":"2026-10-25T09:00:00+02:00","prompt":"p","session_id":"s9"}""",
            body: $$"""{"wakeup_id":"{{WakeUpId}}","fire_at":"2026-10-25T07:00:00+00:00"}""");

        await Assert.That(call.IsError).IsFalse();
        await Assert.That(JsonNode.DeepEquals(SentBody(call), JsonNode.Parse(
            """{"session_id":"s9","prompt":"p","caller_agent_id":"agent-7","fire_at":"2026-10-25T09:00:00.0000000+02:00"}"""))).IsTrue();
    }

    [Test]
    [Arguments("""{"prompt":"p"}""", "Give exactly one of delay_minutes and at.")]
    [Arguments("""{"prompt":"p","delay_minutes":5,"at":"2026-10-25T09:00:00Z"}""", "Give exactly one of delay_minutes and at.")]
    [Arguments("""{"prompt":"p","at":"tomorrow"}""", "'at' must be a date and time with an offset, such as 2026-10-24T15:00:00Z.")]
    [Arguments("""{"prompt":"p","at":"2026-10-25T09:00:00"}""", "'at' must be a date and time with an offset, such as 2026-10-24T15:00:00Z.")]
    [Arguments("""{"prompt":"p","delay_minutes":"5"}""", "'delay_minutes' must be an integer within int range.")]
    [Arguments("""{"delay_minutes":5}""", "'prompt' is required.")]
    [Arguments("""{"wakeup_id":"not-a-guid"}""", "'wakeup_id' must be a wake-up id as schedule_wakeup or list_wakeups returned it.")]
    public async Task A_malformed_call_is_refused_and_sends_nothing(string arguments, string error) {
        var tool = arguments.Contains("wakeup_id") ? "cancel_wakeup" : "schedule_wakeup";

        var call = await CallAsync(tool, arguments);

        await Assert.That(call.IsError).IsTrue();
        await Assert.That(call.Text).IsEqualTo($"Error: {error}");
        await Assert.That(call.Request).IsNull();
    }

    [Test]
    public async Task List_reads_the_sessions_wake_ups_and_renders_each() {
        var call = await CallAsync("list_wakeups", "{}", body: $$"""
            {"wakeups":[
              {"wakeup_id":"{{WakeUpId}}","state":"pending","fire_at":"2026-10-24T15:20:00+00:00","scheduled_at":"2026-10-24T15:00:00+00:00","prompt":"Read the findings.","outcome":null,"settled_at":null},
              {"wakeup_id":"00000000-0000-0000-0000-000000000001","state":"dropped","fire_at":"2026-10-24T09:00:00+00:00","scheduled_at":"2026-10-24T08:00:00+00:00","prompt":"Check CI.","outcome":"agent_gone","settled_at":"2026-10-24T09:00:15+00:00"}
            ]}
            """);

        await Assert.That(call.IsError).IsFalse();
        await Assert.That(call.Request!.RequestMessage.Method).IsEqualTo("GET");
        await Assert.That(call.Request.RequestMessage.Path).IsEqualTo("/api/wakeups");
        await Assert.That(SentQuery(call)).IsEqualTo("session_id=s1");
        await Assert.That(call.Text).IsEqualTo($"""
            wakeup_id: {WakeUpId}
            state: pending
            fire_at: 2026-10-24T15:20:00+00:00
            scheduled_at: 2026-10-24T15:00:00+00:00
            prompt: Read the findings.

            wakeup_id: 00000000-0000-0000-0000-000000000001
            state: dropped
            outcome: agent_gone
            fire_at: 2026-10-24T09:00:00+00:00
            scheduled_at: 2026-10-24T08:00:00+00:00
            settled_at: 2026-10-24T09:00:15+00:00
            prompt: Check CI.
            """.ReplaceLineEndings("\n"));
    }

    [Test]
    [Arguments("""{"wakeups":[]}""", false, "This session has no wake-ups pending, and none settled in the last 24 hours.")]
    [Arguments("""{"flows":[]}""", true, "Error: the server's wake-up list (GET /api/wakeups) could not be read.")]
    [Arguments("<html></html>", true, "Error: the server's wake-up list (GET /api/wakeups) could not be read.")]
    [Arguments("""{"wakeups":[null]}""", true, "Error: the server's wake-up list (GET /api/wakeups) could not be read.")]
    [Arguments("""{"wakeups":[3]}""", true, "Error: the server's wake-up list (GET /api/wakeups) could not be read.")]
    [Arguments("""{"wakeups":[{}]}""", true, "Error: the server's wake-up list (GET /api/wakeups) could not be read.")]
    [Arguments("""{"wakeups":[{"wakeup_id":"w","state":"pending"},{"state":"pending"}]}""", true, "Error: the server's wake-up list (GET /api/wakeups) could not be read.")]
    [Arguments("""{"wakeups":[{"wakeup_id":7,"state":"pending"}]}""", true, "Error: the server's wake-up list (GET /api/wakeups) could not be read.")]
    public async Task List_answers_an_empty_or_unreadable_body_plainly(string body, bool isError, string text) {
        var call = await CallAsync("list_wakeups", "{}", body: body);

        await Assert.That(call.IsError).IsEqualTo(isError);
        await Assert.That(call.Text).IsEqualTo(text);
    }

    [Test]
    public async Task Cancel_posts_the_session_to_that_wake_ups_cancel_route() {
        var call = await CallAsync("cancel_wakeup", $$"""{"wakeup_id":"{{WakeUpId}}"}""", body: """{"ok":true}""");

        await Assert.That(call.IsError).IsFalse();
        await Assert.That(call.Text).IsEqualTo($"Cancelled wake-up {WakeUpId}.");
        await Assert.That(call.Request!.RequestMessage.Method).IsEqualTo("POST");
        await Assert.That(call.Request.RequestMessage.Path).IsEqualTo($"/api/wakeups/{WakeUpId}/cancel");
        await Assert.That(JsonNode.DeepEquals(SentBody(call), JsonNode.Parse("""{"session_id":"s1"}"""))).IsTrue();
    }

    /// <summary>A 2xx that is not the route's confirmation, such as a proxy's page, proves nothing was cancelled.</summary>
    [Test]
    [Arguments("<html></html>")]
    [Arguments("{}")]
    [Arguments("""{"ok":false}""")]
    [Arguments("""{"ok":"true"}""")]
    public async Task A_cancel_answer_without_the_confirmation_is_not_reported_as_cancelled(string body) {
        var call = await CallAsync("cancel_wakeup", $$"""{"wakeup_id":"{{WakeUpId}}"}""", body: body);

        await Assert.That(call.IsError).IsTrue();
        await Assert.That(call.Text).IsEqualTo(
            $"Error: the server's answer to the cancel of wake-up {WakeUpId} could not be read, so whether it was cancelled is unknown. Call list_wakeups to see.");
    }

    /// <summary>Every refusal the server codes reaches the agent with its code and the server's own sentence.</summary>
    [Test]
    [Arguments("schedule_wakeup", 400, "invalid_request")]
    [Arguments("schedule_wakeup", 403, "session_not_owned")]
    [Arguments("schedule_wakeup", 422, "session_not_hosted")]
    [Arguments("schedule_wakeup", 422, "flow_participant")]
    [Arguments("schedule_wakeup", 422, "pending_limit_reached")]
    [Arguments("schedule_wakeup", 422, "schedule_limit_reached")]
    [Arguments("schedule_wakeup", 409, "contended")]
    [Arguments("schedule_wakeup", 503, "not_recorded")]
    [Arguments("list_wakeups", 403, "session_not_owned")]
    [Arguments("cancel_wakeup", 404, "wakeup_not_found")]
    [Arguments("cancel_wakeup", 422, "already_firing")]
    [Arguments("cancel_wakeup", 422, "already_settled")]
    public async Task A_coded_refusal_is_relayed_in_the_servers_words(string tool, int status, string code) {
        var call = await CallAsync(tool, $$"""{"delay_minutes":5,"prompt":"p","wakeup_id":"{{WakeUpId}}"}""",
            status, $$"""{"error":"{{code}}","message":"The server's sentence."}""");

        await Assert.That(call.IsError).IsTrue();
        await Assert.That(call.Text).IsEqualTo($"Error ({code}): The server's sentence.");
    }

    /// <summary>With the gate off nothing is mapped under <c>/api/wakeups</c>: a POST meets the page fallback's 405, a
    /// GET a 404, and neither carries a refusal code.</summary>
    [Test]
    [Arguments("schedule_wakeup", 405, "")]
    [Arguments("schedule_wakeup", 404, "")]
    [Arguments("list_wakeups", 404, """{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.5","title":"Not Found","status":404}""")]
    [Arguments("cancel_wakeup", 405, """{"title":"Method Not Allowed","status":405}""")]
    public async Task A_server_without_wake_ups_says_so(string tool, int status, string body) {
        var call = await CallAsync(tool, $$"""{"delay_minutes":5,"prompt":"p","wakeup_id":"{{WakeUpId}}"}""", status, body);

        await Assert.That(call.IsError).IsTrue();
        await Assert.That(call.Text).StartsWith("Error: this server does not support wake-ups");
    }

    /// <summary>An uncoded 5xx does not say whether a schedule or cancel was committed, so the agent is sent to the list
    /// before it retries. A coded one, such as <c>not_recorded</c>, says nothing changed and is relayed as it is.</summary>
    [Test]
    [Arguments("schedule_wakeup", 500, "Error: HTTP 500 — boom\nThe wake-up may or may not have been scheduled: call list_wakeups before scheduling again.")]
    [Arguments("cancel_wakeup", 502, "Error: HTTP 502 — boom\nWhether the wake-up was cancelled is unknown: call list_wakeups before cancelling again.")]
    public async Task An_uncoded_server_failure_on_a_change_says_its_outcome_is_unknown(string tool, int status, string text) {
        var call = await CallAsync(tool, $$"""{"delay_minutes":5,"prompt":"p","wakeup_id":"{{WakeUpId}}"}""", status, "boom");

        await Assert.That(call.IsError).IsTrue();
        await Assert.That(call.Text).IsEqualTo(text);
    }

    [Test]
    public async Task An_uncoded_failure_is_shown_with_its_status() {
        var call = await CallAsync("list_wakeups", "{}", 500, "boom");

        await Assert.That(call.IsError).IsTrue();
        await Assert.That(call.Text).IsEqualTo("Error: HTTP 500 — boom");
    }

    /// <summary>A schedule whose answer never arrived may have been recorded, so the agent is sent to the list rather
    /// than told to schedule again.</summary>
    [Test]
    public async Task A_schedule_with_no_answer_says_it_may_have_been_recorded() {
        using var dead = WireMockServer.Start();
        var url = dead.Url!;
        dead.Stop();

        var call = await CallAsync("schedule_wakeup", """{"delay_minutes":5,"prompt":"p"}""", url: url);

        await Assert.That(call.IsError).IsTrue();
        await Assert.That(call.Text).Contains("may or may not have been scheduled");
        await Assert.That(call.Text).Contains("list_wakeups");
    }
}
