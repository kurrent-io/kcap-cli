using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.PrDetection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class McpFlowsServerStartAgentTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempDir]        public required TempDir        Tmp    { get; init; }

    const string Requested = """
        {"status":"requested","agent_id":"a1b2c3d4","url":"https://cap.test/agents/a1b2c3d4","daemon":"mac-studio",
         "repo_path":"/Users/x/dev/repo","vendor":"claude","model":"default","work_item":{"id":null,"reason":"none"}}
        """;

    McpFlowsServer Server() =>
        new(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root),
            new FixedCapacitorHttpClient(), NoTelemetry.Startup, router: new GitProviderRouter(), workdir: new WorkingDirectory(AppContext.BaseDirectory), time: TimeProvider.System);

    string Cwd() {
        Tmp.CreateDir("repo", ".git");

        return Tmp.CreateDir("repo", "src").Path;
    }

    JsonObject ToolCall(JsonObject? arguments = null) => new() {
        ["params"] = new JsonObject {
            ["name"]      = "start_agent",
            ["arguments"] = arguments ?? new JsonObject { ["cwd"] = Cwd(), ["prompt"] = "Fix the retry.", ["title"] = "Fix the retry", ["work_item"] = "none" }
        }
    };

    static (string Text, bool IsError) Result(string response) {
        var result = JsonNode.Parse(response)!["result"]!;

        return (result["content"]![0]!["text"]!.GetValue<string>(), result["isError"]?.GetValue<bool>() ?? false);
    }

    static WireMockServer Answering(int status, string body) {
        var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/agents/start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(status).WithBody(body));

        return server;
    }

    async Task<(string Text, bool IsError)> CallAsync(
            WireMockServer server, JsonObject? arguments = null, string? session = "s1", string? driver = "claude", string? agent = null) {
        using var client = new HttpClient();

        return Result(await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCall(arguments), client, server.Url!, cwd: "/elsewhere", repoRoot: null, repoInfo: null,
            requestingSessionId: session, driverVendor: driver, callerAgentId: agent));
    }

    static JsonObject SentBody(WireMockServer server) =>
        JsonNode.Parse(server.LogEntries.Single().RequestMessage.Body!)!.AsObject();

    /// <summary>One request is the whole call: the tool reads nothing back, so a second entry here
    /// would be a poll.</summary>
    [Test]
    public async Task A_start_posts_one_request_and_reports_requested() {
        using var server = Answering(200, Requested);

        var (text, isError) = await CallAsync(server);

        await Assert.That(isError).IsFalse();
        await Assert.That(text).Contains("status: requested");
        await Assert.That(text).Contains("agent_id: a1b2c3d4");
        await Assert.That(server.LogEntries.Count).IsEqualTo(1);

        var body = SentBody(server);
        await Assert.That(body["session_id"]!.GetValue<string>()).IsEqualTo("s1");
        await Assert.That(body["vendor"]!.GetValue<string>()).IsEqualTo("claude");
        await Assert.That(body["work_item"]!.GetValue<string>()).IsEqualTo("none");
        await Assert.That(body["prompt"]!.GetValue<string>()).IsEqualTo("Fix the retry.");
        await Assert.That(body["repo_path"]!.GetValue<string>()).IsEqualTo(Tmp.PathTo("repo"));
        await Assert.That(body["caller_agent_id"]).IsNull();
        await Assert.That(body["model"]).IsNull();
        await Assert.That(body["daemon"]).IsNull();
    }

    [Test]
    [Arguments("{\"status\":\"requested\",\"agent_id\":\"agent\"}", true)]
    [Arguments("{\"status\":\"requested\",\"agent_id\":\"agent\",\"loose_end_claim_id\":\" \" ,\"dispatch_state\":\"sent\"}", true)]
    [Arguments("{\"status\":\"requested\",\"agent_id\":\"agent\",\"loose_end_claim_id\":\"claim\"}", true)]
    [Arguments("{\"status\":\"requested\",\"agent_id\":\"agent\",\"loose_end_claim_id\":\"claim\",\"dispatch_state\":\"sent\"}", false)]
    [Arguments("{\"status\":\"pending\",\"agent_id\":\"agent\",\"loose_end_claim_id\":\"claim\",\"dispatch_state\":\"unknown\"}", false)]
    [Arguments("{\"status\":\"pending\",\"agent_id\":\"agent\",\"loose_end_claim_id\":\"claim\",\"dispatch_state\":\"not_sent\"}", false)]
    public async Task A_loose_end_start_requires_a_claim_receipt_and_never_retries(string answer, bool error) {
        using var server = Answering(200, answer);
        var arguments = new JsonObject { ["cwd"] = Cwd(), ["prompt"] = "Fix the retry.", ["title"] = "Fix the retry", ["work_item"] = "le:end" };
        var (text, isError) = await CallAsync(server, arguments);
        await Assert.That(isError).IsEqualTo(error);
        await Assert.That(server.LogEntries.Count).IsEqualTo(1);
        if (error) await Assert.That(text).IsEqualTo(StartAgentTool.UnreadableAnswer);
        else await Assert.That(text).Contains("loose_end_claim_id: claim");
    }

    [Test]
    public async Task A_machine_with_no_persisted_id_sends_none_and_is_left_without_one() {
        using var server = Answering(200, Requested);

        await CallAsync(server);

        await Assert.That(SentBody(server)["machine_id"]).IsNull();
        await Assert.That(new MachineId(Config.Root).ReadPersisted()).IsNull();
    }

    [Test]
    public async Task The_persisted_machine_id_is_sent() {
        var machine = new MachineId(Config.Root).Get();
        using var server = Answering(200, Requested);

        await CallAsync(server);

        await Assert.That(SentBody(server)["machine_id"]!.GetValue<string>()).IsEqualTo(machine);
    }

    [Test]
    public async Task The_hosted_agent_id_goes_as_caller_agent_id() {
        using var server = Answering(200, Requested);

        await CallAsync(server, agent: "0f1e2d3c");

        await Assert.That(SentBody(server)["caller_agent_id"]!.GetValue<string>()).IsEqualTo("0f1e2d3c");
    }

    [Test]
    public async Task A_refusal_reaches_the_caller_in_the_servers_words() {
        using var server = Answering(409, """{"error":"daemon_at_capacity","message":"The daemon has no free slot.","active":4,"max":4}""");

        var (text, isError) = await CallAsync(server);

        await Assert.That(isError).IsTrue();
        await Assert.That(text).IsEqualTo("Error (daemon_at_capacity): The daemon has no free slot.\nactive: 4\nmax: 4");
        await Assert.That(server.LogEntries.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_server_without_the_route_is_said_to_be_unable_to_start_agents() {
        using var server = Answering(405, "");

        var (text, isError) = await CallAsync(server);

        await Assert.That(isError).IsTrue();
        await Assert.That(text).IsEqualTo(StartAgentTool.ServerCannotStartAgents);
    }

    [Test]
    public async Task Unauthorized_is_reported_as_every_other_tool_reports_it() {
        using var server = Answering(401, "");
        var expected = await AuthRejectionNotice.ForPersistentUnauthorizedAsync(
            AuthFixtures.NewTokenStore(Config.Root), Resolutions.None(Config.Root).Name, server.Url!.TrimEnd('/'), TimeProvider.System);

        var (text, isError) = await CallAsync(server);

        await Assert.That(isError).IsTrue();
        await Assert.That(text).IsEqualTo(expected);
    }

    [Test]
    public async Task A_call_that_is_refused_locally_sends_nothing() {
        using var server = Answering(200, Requested);
        var arguments = new JsonObject { ["cwd"] = Cwd(), ["prompt"] = "Fix the retry.", ["title"] = "Fix the retry" };

        var (text, isError) = await CallAsync(server, arguments);

        await Assert.That(isError).IsTrue();
        await Assert.That(text).IsEqualTo("Error: 'work_item' is required.");
        await Assert.That(server.LogEntries.Count).IsEqualTo(0);
    }

    /// <summary>The MCP loop is serial, so a server that stops answering must cost one bounded call.
    /// The text must not read as a plain failure: the request may have been acted on.</summary>
    [Test]
    public async Task A_server_that_stops_answering_is_cut_off_and_the_outcome_is_called_unknown() {
        var clock = new VirtualFlowRetryClock();
        using var client = new HttpClient(new HoldsUntilCancelled(clock));

        var (text, isError) = Result(await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCall(), client, "http://unanswering.test", cwd: "/elsewhere", repoRoot: null, repoInfo: null,
            clock: clock, requestingSessionId: "s1", driverVendor: "claude"));

        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("timed out after 60 s");
        await Assert.That(text).EndsWith(StartAgentTool.OutcomeUnknown);
    }

    [Test]
    public async Task The_options_list_the_daemons_on_this_machine_and_start_nothing() {
        var machine = new MachineId(Config.Root).Get();
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/daemons").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody($$"""
                [{"name":"mac-studio","connected":true,"machine_id":"{{machine}}","active_agents":0,"max_agents":4,"supported_vendors":["claude","codex"]},
                 {"name":"laptop","connected":true,"machine_id":"elsewhere","active_agents":0,"max_agents":4,"supported_vendors":["gemini"]}]
                """));
        using var client = new HttpClient();

        var (text, isError) = Result(await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, new JsonObject { ["params"] = new JsonObject { ["name"] = "list_start_agent_options" } },
            client, server.Url!, cwd: "/elsewhere", repoRoot: null, repoInfo: null, driverVendor: "codex"));

        await Assert.That(isError).IsFalse();
        await Assert.That(text).StartsWith("harness running this session: codex\n");
        await Assert.That(text).Contains("- mac-studio: 0 of 4 agent slots in use; harnesses: claude, codex");
        await Assert.That(text).DoesNotContain("laptop");
        await Assert.That(server.LogEntries.Select(e => e.RequestMessage.Method).ToArray()).IsEquivalentTo(new[] { "GET" });
    }

    sealed class HoldsUntilCancelled(VirtualFlowRetryClock clock) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            clock.Advance(TimeSpan.FromHours(1));
            ct.ThrowIfCancellationRequested();

            throw new InvalidOperationException("the start carried no timeout");
        }
    }
}
