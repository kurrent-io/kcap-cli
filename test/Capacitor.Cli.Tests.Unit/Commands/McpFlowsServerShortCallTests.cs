using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Capacitor.Cli.PrDetection;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>A caller whose tool calls are cut short (Muse's helper stops each at 60 s) drives a flow
/// with <c>wait: false</c> on start and send and a bounded <c>timeout_s</c> on the status wait, with no
/// repository at all.</summary>
public class McpFlowsServerShortCallTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    McpFlowsServer Server() =>
        new(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root),
            new FixedCapacitorHttpClient(), NoTelemetry.Startup, router: new GitProviderRouter(), workdir: new WorkingDirectory(AppContext.BaseDirectory), time: TimeProvider.System);

    const string FlowRunId = "flow-doc-1";

    const string RunningBody =
        $$"""{"flow_run_id":"{{FlowRunId}}","status":"running","round_id":"r1","round_number":1,"result_kind":null,"result_text":null,"applied_reviewer_vendor":"claude"}""";

    static JsonObject ToolCallRequest(string toolName, JsonObject arguments) => new() {
        ["params"] = new JsonObject { ["name"] = toolName, ["arguments"] = arguments }
    };

    static JsonObject StartArguments(JsonNode? wait) {
        var arguments = new JsonObject {
            ["definition_id"] = "doc-review",
            ["target_kind"]   = "spec",
            ["target_ref"]    = "onboarding-spec",
            ["target_title"]  = "Onboarding spec",
            ["session_title"] = "Review the onboarding spec",
            ["context"]       = "# Onboarding\nThe whole document, inline.",
            ["vendor"]        = "claude"
        };
        if (wait is not null) arguments["wait"] = wait;
        return arguments;
    }

    static (string Text, bool IsError) Unwrap(string response) {
        var result = JsonNode.Parse(response)!.AsObject()["result"]!.AsObject();
        return (result["content"]![0]!["text"]!.GetValue<string>(), result["isError"]?.GetValue<bool>() ?? false);
    }

    static WireMockServer StartServer() {
        var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(new WildcardMatcher("/api/flows/review/start*")).UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(RunningBody));
        server.Given(Request.Create().WithPath($"/api/flows/{FlowRunId}/rounds").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                  .WithBody(RunningBody.Replace("\"round_number\":1", "\"round_number\":2")));
        server.Given(Request.Create().WithPath($"/api/flows/{FlowRunId}").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                  .WithBody($$"""{"flow_run_id":"{{FlowRunId}}","status":"running","definition_id":"doc-review","target_title":"t","round_count":1}"""));
        return server;
    }

    static int Gets(WireMockServer server) =>
        server.LogEntries.Count(e => e.RequestMessage.Path == $"/api/flows/{FlowRunId}" && e.RequestMessage.Method == "GET");

    [Test]
    public async Task A_start_with_wait_false_returns_the_run_id_without_polling() {
        using var server = StartServer();
        using var client = new HttpClient();

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("start_flow", StartArguments(false)),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null, clock: new VirtualFlowRetryClock());

        var (text, isError) = Unwrap(response);
        await Assert.That(isError).IsFalse();
        await Assert.That(text).Contains($"Flow still running for flow_run_id {FlowRunId} (round 1)");
        await Assert.That(Gets(server)).IsEqualTo(0);
    }

    /// <summary>The start carries no repository: the server, not the CLI, decides whether the definition
    /// needs one.</summary>
    [Test]
    public async Task A_start_outside_any_repository_sends_no_repository() {
        using var server = StartServer();
        using var client = new HttpClient();

        await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("start_flow", StartArguments(false)),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null, clock: new VirtualFlowRetryClock());

        var body = JsonNode.Parse(server.LogEntries.Single(e => e.RequestMessage.Method == "POST").RequestMessage.Body!)!.AsObject();
        await Assert.That(body["repo_owner"]?.GetValue<string>()).IsNull();
        await Assert.That(body["repo_name"]?.GetValue<string>()).IsNull();
        await Assert.That(body["repo_path"]?.GetValue<string>()).IsNull();
    }

    [Test]
    public async Task A_start_without_wait_still_polls_the_round() {
        using var server = StartServer();
        using var client = new HttpClient();

        await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("start_flow", StartArguments(null)),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null, clock: new VirtualFlowRetryClock());

        await Assert.That(Gets(server)).IsGreaterThan(0);
    }

    [Test]
    public async Task A_send_with_wait_false_returns_without_polling() {
        using var server = StartServer();
        using var client = new HttpClient();

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!,
            ToolCallRequest("send_to_participant", new JsonObject {
                ["flow_run_id"] = FlowRunId, ["participant"] = "reviewer", ["message"] = "Revised document", ["wait"] = false
            }),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null, clock: new VirtualFlowRetryClock());

        var (text, isError) = Unwrap(response);
        await Assert.That(isError).IsFalse();
        await Assert.That(text).Contains($"Flow still running for flow_run_id {FlowRunId} (round 2)");
        await Assert.That(Gets(server)).IsEqualTo(0);
    }

    /// <summary>A server busy past the short-call budget must not hold the call beyond what the caller
    /// allows; the busy start minted nothing, so the error is the caller's cue to call again.</summary>
    [Test]
    public async Task A_start_with_wait_false_stops_retrying_a_busy_server_within_the_short_budget() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(new WildcardMatcher("/api/flows/review/start*")).UsingPost())
              .RespondWith(Response.Create().WithStatusCode(409).WithBody(
                  """{"error":"flow_settlement_busy","message":"A concurrent settlement operation is racing this flow run."}"""));
        using var client = new HttpClient();
        var clock = new VirtualFlowRetryClock();

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("start_flow", StartArguments(false)),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null, clock: clock);

        var (text, isError) = Unwrap(response);
        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("flow_settlement_busy");
        await Assert.That(clock.Elapsed).IsLessThanOrEqualTo(McpFlowsServer.ShortCallSendBudget);
    }

    [Test]
    public async Task A_status_wait_ends_within_timeout_s() {
        using var server = StartServer();
        using var client = new HttpClient();
        var clock = new VirtualFlowRetryClock();

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!,
            ToolCallRequest("get_flow_status", new JsonObject { ["flow_run_id"] = FlowRunId, ["wait"] = true, ["timeout_s"] = 50 }),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null, clock: clock);

        var (text, isError) = Unwrap(response);
        await Assert.That(isError).IsFalse();
        await Assert.That(text).Contains("Flow still running");
        await Assert.That(clock.Elapsed).IsLessThanOrEqualTo(TimeSpan.FromSeconds(53));
        await Assert.That(Gets(server)).IsGreaterThan(1);
    }

    [Test]
    [Arguments("0")]
    [Arguments("211")]
    [Arguments("\"soon\"")]
    public async Task An_out_of_range_timeout_s_is_a_clean_error_before_any_request(string timeout) {
        using var server = StartServer();
        using var client = new HttpClient();

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!,
            ToolCallRequest("get_flow_status", new JsonObject { ["flow_run_id"] = FlowRunId, ["wait"] = true, ["timeout_s"] = JsonNode.Parse(timeout) }),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null, clock: new VirtualFlowRetryClock());

        var (text, isError) = Unwrap(response);
        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("timeout_s must be an integer from 1 to 210");
        await Assert.That(Gets(server)).IsEqualTo(0);
    }

    [Test]
    public async Task A_non_boolean_wait_on_start_is_a_clean_error() {
        using var server = StartServer();
        using var client = new HttpClient();

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("start_flow", StartArguments("no")),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null, clock: new VirtualFlowRetryClock());

        var (text, isError) = Unwrap(response);
        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("wait must be a boolean");
    }
}
