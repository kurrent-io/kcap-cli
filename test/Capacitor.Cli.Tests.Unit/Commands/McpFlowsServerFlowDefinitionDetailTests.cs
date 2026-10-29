using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.PrDetection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class McpFlowsServerFlowDefinitionDetailTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    McpFlowsServer Server() =>
        new(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root),
            new FixedCapacitorHttpClient(), NoTelemetry.Startup, router: new GitProviderRouter(), workdir: new WorkingDirectory(AppContext.BaseDirectory), time: TimeProvider.System);

    static JsonObject ToolCall(JsonObject arguments) => new() {
        ["params"] = new JsonObject { ["name"] = "get_flow_definition", ["arguments"] = arguments }
    };

    static (string Text, bool IsError) Result(string response) {
        var result = JsonNode.Parse(response)!["result"]!;

        return (result["content"]![0]!["text"]!.GetValue<string>(), result["isError"]?.GetValue<bool>() ?? false);
    }

    static WireMockServer Serving(string id, int status, string body, string contentType = "application/json") {
        var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath($"/api/flows/definitions/{id}").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", contentType).WithBody(body));

        return server;
    }

    async Task<(string Text, bool IsError)> CallAsync(WireMockServer server, string id) {
        using var client = new HttpClient();

        return Result(await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCall(new JsonObject { ["definition_id"] = id }), client, server.Url!, cwd: "/r", repoRoot: "/r", repoInfo: null));
    }

    const string CodeReview = """
        {"id":"code-review","version":4,"description":"Review code changes.","is_single_participant":true,
         "participants":[{"role":"reviewer","vendor":null,"model":"default"}],
         "offer":"proactive","when_to_use":"After a change.","driver_guide":"## What to submit\nThe commit range."}
        """;

    [Test]
    public async Task Renders_the_guide_under_the_definition() {
        using var server = Serving("code-review", 200, CodeReview);

        var (text, isError) = await CallAsync(server, "code-review");

        await Assert.That(isError).IsFalse();
        await Assert.That(text).Contains("code-review (v4) — single participant");
        await Assert.That(text).Contains("  reviewer: vendor unset");
        await Assert.That(text).Contains("when to use (offer proactively): After a change.");
        await Assert.That(text).Contains("Driver guide:\n## What to submit\nThe commit range.");
    }

    [Test]
    public async Task A_definition_without_a_guide_says_so() {
        using var server = Serving("plain", 200, """{"id":"plain","participants":[{"role":"reviewer","model":"default"}],"offer":"on_request"}""");

        var (text, isError) = await CallAsync(server, "plain");

        await Assert.That(isError).IsFalse();
        await Assert.That(text).Contains("This definition publishes no driver guide");
    }

    [Test]
    public async Task An_unknown_id_is_an_error_naming_the_listing() {
        using var server = Serving("nope", 404, """{"status":404,"detail":"Flow definition 'nope' is not available."}""", "application/problem+json");

        var (text, isError) = await CallAsync(server, "nope");

        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("Flow definition 'nope' is not available.");
        await Assert.That(text).Contains("list_flow_definitions");
    }

    [Test]
    public async Task An_older_server_without_the_route_is_reported_not_errored() {
        using var server = Serving("code-review", 404, "", "text/plain");

        var (text, isError) = await CallAsync(server, "code-review");

        await Assert.That(isError).IsFalse();
        await Assert.That(text).IsEqualTo(McpFlowsServer.ServerPublishesNoGuides);
    }

    [Test]
    public async Task A_catalog_that_has_not_projected_is_a_retryable_refusal() {
        using var server = Serving("code-review", 409, """{"error":"server_catching_up","message":"Flows are temporarily unavailable."}""");

        var (text, isError) = await CallAsync(server, "code-review");

        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains(McpFlowsServer.ServerCatchingUpGuidance);
    }

    [Test]
    public async Task A_missing_definition_id_is_rejected_without_a_request() {
        using var server = Serving("x", 200, CodeReview);
        using var client = new HttpClient();

        var (text, isError) = Result(await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCall(new JsonObject()), client, server.Url!, cwd: "/r", repoRoot: "/r", repoInfo: null));

        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("definition_id is required");
        await Assert.That(server.LogEntries.Count).IsEqualTo(0);
    }

    [Test]
    public async Task An_id_is_escaped_into_one_path_segment() {
        using var server = WireMockServer.Start();
        using var client = new HttpClient();

        await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCall(new JsonObject { ["definition_id"] = "a/b" }), client, server.Url!, cwd: "/r", repoRoot: "/r", repoInfo: null);

        await Assert.That(server.LogEntries.Single().RequestMessage.Url).Contains("/api/flows/definitions/a%2Fb");
    }

    [Test]
    public async Task The_tool_is_read_only_and_requires_definition_id() {
        var tool = McpFlowsServer.BuildToolsList().Single(t => t.Name == "get_flow_definition");

        await Assert.That(tool.Annotations).IsEqualTo(McpToolAnnotations.Read);
        await Assert.That(tool.InputSchema.Required).IsEquivalentTo(new[] { "definition_id" });
        await Assert.That(tool.Description).Contains("before start_flow");
    }
}
