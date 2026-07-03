using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit;

public class McpFlowResultServerTests {
    static JsonObject Args(string? roundToken = "round-1", string? kind = "findings", string? findings = "1. issue") {
        var o = new JsonObject();
        if (roundToken is not null) o["round_token"] = (JsonNode?)roundToken;
        if (kind is not null) o["kind"] = (JsonNode?)kind;
        if (findings is not null) o["findings"] = (JsonNode?)findings;
        return o;
    }

    static Func<TimeSpan, Task> NoDelay(List<TimeSpan> recorded) => ts => { recorded.Add(ts); return Task.CompletedTask; };

    [Test]
    public async Task Happy_path_posts_snake_case_body_and_reports_round() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/reviewer/result").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"flow_run_id":"f1","round_id":"r1","round_number":2}"""));
        using var client = new HttpClient();

        var delays = new List<TimeSpan>();
        var (text, isError) = await McpFlowResultServer.SubmitCoreAsync(client, server.Url!, "agent-1", Args(), NoDelay(delays));

        await Assert.That(isError).IsFalse();
        await Assert.That(text).IsEqualTo("Result recorded for round 2. You may end your reply now.");

        var body = server.LogEntries.Single().RequestMessage.Body!;
        await Assert.That(body).Contains("\"agent_id\"");
        await Assert.That(body).Contains("\"round_token\"");
        await Assert.That(body).Contains("\"kind\"");
        await Assert.That(body).Contains("\"text\"");
        await Assert.That(body).Contains("agent-1");
    }

    [Test]
    public async Task Clean_kind_omits_null_text_from_the_wire() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/reviewer/result").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"flow_run_id":"f1","round_id":"r1","round_number":1}"""));
        using var client = new HttpClient();

        var (_, isError) = await McpFlowResultServer.SubmitCoreAsync(client, server.Url!, "agent-1", Args(kind: "clean", findings: null), NoDelay([]));

        await Assert.That(isError).IsFalse();
        // McpJsonContext ignores null when writing, so a clean submit must not carry a text key.
        await Assert.That(server.LogEntries.Single().RequestMessage.Body!).DoesNotContain("\"text\"");
    }

    [Test]
    public async Task Retryable_no_active_flow_retries_then_succeeds() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/reviewer/result").UsingPost())
              .InScenario("launch-race")
              .WillSetStateTo("second")
              .RespondWith(Response.Create().WithStatusCode(404).WithBody("""{"error":"no_active_flow","message":"not registered yet"}"""));
        server.Given(Request.Create().WithPath("/api/flows/reviewer/result").UsingPost())
              .InScenario("launch-race")
              .WhenStateIs("second")
              .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"flow_run_id":"f1","round_id":"r1","round_number":1}"""));
        using var client = new HttpClient();

        var delays = new List<TimeSpan>();
        var (text, isError) = await McpFlowResultServer.SubmitCoreAsync(client, server.Url!, "agent-1", Args(), NoDelay(delays));

        await Assert.That(isError).IsFalse();
        await Assert.That(text).Contains("round 1");
        await Assert.That(delays).HasCount().EqualTo(1);
        await Assert.That(delays[0]).IsEqualTo(TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task Retryable_error_exhausts_after_five_attempts_with_fallback_hint() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/reviewer/result").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(409).WithBody("""{"error":"no_open_round","message":"no round awaiting a result"}"""));
        using var client = new HttpClient();

        var delays = new List<TimeSpan>();
        var (text, isError) = await McpFlowResultServer.SubmitCoreAsync(client, server.Url!, "agent-1", Args(), NoDelay(delays));

        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("no round awaiting a result");
        await Assert.That(text).Contains("fall back to the marker");
        await Assert.That(delays).HasCount().EqualTo(4); // 5 attempts = 4 delays
        await Assert.That(server.LogEntries.Count()).IsEqualTo(5);
    }

    [Test]
    public async Task Stale_round_token_fails_immediately_without_fallback_hint() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/reviewer/result").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(409).WithBody("""{"error":"stale_round_token","message":"That round is already closed and a newer round is open. Discard this result entirely — do NOT emit a FINDINGS:/NO FINDINGS marker for it — and respond only to the newest prompt you received."}"""));
        using var client = new HttpClient();

        var delays = new List<TimeSpan>();
        var (text, isError) = await McpFlowResultServer.SubmitCoreAsync(client, server.Url!, "agent-1", Args(roundToken: "round-0"), NoDelay(delays));

        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("Discard this result entirely");
        await Assert.That(text).DoesNotContain("fall back to the marker");
        await Assert.That(delays).HasCount().EqualTo(0);
        await Assert.That(server.LogEntries.Count()).IsEqualTo(1);
    }

    [Test]
    public async Task Validation_failures_do_not_hit_the_network() {
        using var server = WireMockServer.Start();
        using var client = new HttpClient();

        var missingToken = await McpFlowResultServer.SubmitCoreAsync(client, server.Url!, "agent-1", Args(roundToken: null), NoDelay([]));
        var badKind      = await McpFlowResultServer.SubmitCoreAsync(client, server.Url!, "agent-1", Args(kind: "bogus"), NoDelay([]));
        var noFindings   = await McpFlowResultServer.SubmitCoreAsync(client, server.Url!, "agent-1", Args(findings: null), NoDelay([]));

        await Assert.That(missingToken.IsError).IsTrue();
        await Assert.That(missingToken.Text).Contains("round_token");
        await Assert.That(badKind.IsError).IsTrue();
        await Assert.That(noFindings.IsError).IsTrue();
        await Assert.That(server.LogEntries.Count()).IsEqualTo(0);
    }

    [Test]
    public async Task RunAsync_exits_2_when_agent_env_is_missing() {
        var prior = Environment.GetEnvironmentVariable(McpFlowResultServer.AgentIdEnvVar);
        Environment.SetEnvironmentVariable(McpFlowResultServer.AgentIdEnvVar, null);
        try {
            var exit = await McpFlowResultServer.RunAsync("https://example.test");
            await Assert.That(exit).IsEqualTo(2);
        } finally {
            Environment.SetEnvironmentVariable(McpFlowResultServer.AgentIdEnvVar, prior);
        }
    }
}
