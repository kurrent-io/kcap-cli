using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.PrDetection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class McpFlowsServerFlowDefinitionsTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    McpFlowsServer Server() =>
        new(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root),
            new FixedCapacitorHttpClient(), NoTelemetry.Startup, router: new GitProviderRouter(), workdir: new WorkingDirectory(AppContext.BaseDirectory), time: TimeProvider.System);

    static JsonObject ToolCall() => new() {
        ["params"] = new JsonObject { ["name"] = "list_flow_definitions", ["arguments"] = new JsonObject() }
    };

    static (string Text, bool IsError) Result(string response) {
        var result = JsonNode.Parse(response)!["result"]!;

        return (result["content"]![0]!["text"]!.GetValue<string>(), result["isError"]?.GetValue<bool>() ?? false);
    }

    static WireMockServer Serving(int status, string body) {
        var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/definitions").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(status).WithBody(body));

        return server;
    }

    async Task<(string Text, bool IsError)> CallAsync(WireMockServer server) {
        using var client = new HttpClient();

        return Result(await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCall(), client, server.Url!, cwd: "/r", repoRoot: "/r", repoInfo: null));
    }

    const string TwoDefinitions = """
        {"definitions":[
          {"id":"code-review","version":3,"description":"Review code changes.","is_single_participant":true,
           "participants":[{"role":"reviewer","vendor":null,"model":"default"}]},
          {"id":"review-and-test","version":1,"description":null,"is_single_participant":false,
           "participants":[{"role":"reviewer","vendor":"claude","model":"opus"},{"role":"tester","vendor":"codex","model":"default"}]}
        ]}
        """;

    [Test]
    public async Task Lists_each_definition_led_by_the_id_start_flow_takes() {
        using var server = Serving(200, TwoDefinitions);

        var (text, isError) = await CallAsync(server);

        await Assert.That(isError).IsFalse();
        await Assert.That(text).Contains("flow_definitions (2):");
        await Assert.That(text).Contains("- code-review (v3) — single participant");
        await Assert.That(text).Contains("  Review code changes.");
        await Assert.That(text).Contains("  reviewer: vendor unset");
        await Assert.That(text).Contains("- review-and-test (v1) — 2 participants");
        await Assert.That(text).Contains("send_to_participant");
        await Assert.That(text).Contains("  reviewer: claude, model opus");
        await Assert.That(text).Contains("  tester: codex, model default");
        await Assert.That(text).Contains("definition_id");
    }

    [Test]
    public async Task An_empty_catalog_is_stated_not_errored() {
        using var server = Serving(200, """{"definitions":[]}""");

        var (text, isError) = await CallAsync(server);

        await Assert.That(isError).IsFalse();
        await Assert.That(text).Contains("No flow definition is runnable");
    }

    /// <summary>A 404 is the routing layer of a server that predates the listing: the built-ins still
    /// start, so the driver is told what it can do rather than handed a failure.</summary>
    [Test]
    public async Task An_older_server_without_the_route_is_reported_not_errored() {
        using var server = Serving(404, "");

        var (text, isError) = await CallAsync(server);

        await Assert.That(isError).IsFalse();
        await Assert.That(text).Contains("cannot list flow definitions");
        await Assert.That(text).Contains("spec-review");
        await Assert.That(text).Contains("code-review");
    }

    [Test]
    public async Task A_catalog_that_has_not_projected_is_a_retryable_refusal() {
        using var server = Serving(409, """{"error":"server_catching_up","message":"Flows are temporarily unavailable."}""");

        var (text, isError) = await CallAsync(server);

        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("Error (server_catching_up): Flows are temporarily unavailable.");
        await Assert.That(text).Contains(McpFlowsServer.ServerCatchingUpGuidance);
    }

    [Test]
    public async Task An_unreadable_body_is_an_error() {
        using var server = Serving(200, "not json");

        var (text, isError) = await CallAsync(server);

        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("unreadable flow definition list");
    }

    [Test]
    public async Task A_body_without_a_definitions_array_is_unreadable() {
        await Assert.That(McpFlowsServer.FormatFlowDefinitions("""{"items":[]}""")).IsNull();
        await Assert.That(McpFlowsServer.FormatFlowDefinitions("[]")).IsNull();
    }

    [Test]
    public async Task An_entry_without_an_id_is_skipped_and_the_rest_rendered() {
        var text = McpFlowsServer.FormatFlowDefinitions("""
            {"definitions":[{"version":1},{"id":"only-one","participants":[{"role":"reviewer","model":"default"}]}]}
            """);

        await Assert.That(text).IsNotNull();
        await Assert.That(text!).Contains("- only-one — single participant");
        await Assert.That(text).DoesNotContain("(v1)");
    }

    /// <summary>The MCP loop is serial, so a server that stops answering must cost one bounded lookup, not
    /// every later tool call.</summary>
    [Test]
    public async Task A_server_that_stops_answering_times_out_instead_of_holding_the_tool_loop() {
        var clock = new VirtualFlowRetryClock();
        using var client = new HttpClient(new HoldsUntilCancelled(clock));

        var (text, isError) = Result(await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCall(), client, "http://unanswering.test", cwd: "/r", repoRoot: "/r", repoInfo: null, clock: clock));

        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("listing flow definitions");
        await Assert.That(text).Contains("timed out after 20 s");
    }

    /// <summary>Never answers: moves the virtual clock past every timeout source and then honours the
    /// cancellation that produces, the way a held connection is cut by the lookup's own timeout.</summary>
    sealed class HoldsUntilCancelled(VirtualFlowRetryClock clock) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            clock.Advance(TimeSpan.FromHours(1));
            ct.ThrowIfCancellationRequested();

            throw new InvalidOperationException("the lookup carried no timeout");
        }
    }

    [Test]
    public async Task The_tool_is_read_only_and_says_when_to_call_it() {
        var tool = McpFlowsServer.BuildToolsList().Single(t => t.Name == "list_flow_definitions");

        await Assert.That(tool.Annotations.ReadOnlyHint).IsTrue();
        await Assert.That(tool.InputSchema.Required).IsEmpty();
        await Assert.That(tool.Description).Contains("before start_flow");
        await Assert.That(tool.Description).Contains("does NOT start");
    }
}
