using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.PrDetection;
using Microsoft.Extensions.Time.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// A status call without a <c>flow_run_id</c> resolves the flow from the calling session, or on a
/// harness with no session from the runs this machine recorded for the workspace: the driver
/// that never received the id (its harness aborted the start) or lost it (context compaction) has
/// no other way back to a run that is still working for it.
/// </summary>
public class StatusWithoutFlowRunIdTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string SessionId = "8f3c1c2e4d5a4b6c9d0e1f2a3b4c5d6e";

    McpFlowsServer Server() =>
        new(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root),
            new FixedCapacitorHttpClient(), NoTelemetry.Startup, router: new GitProviderRouter(), workdir: new WorkingDirectory(AppContext.BaseDirectory), time: TimeProvider.System);

    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    static JsonObject ToolCallRequest(string toolName, JsonObject arguments) => new() {
        ["params"] = new JsonObject {
            ["name"]      = toolName,
            ["arguments"] = arguments.DeepClone()
        }
    };

    static (string Text, bool IsError) Unwrap(string response) {
        var result  = JsonNode.Parse(response)!.AsObject()["result"]!.AsObject();
        var text    = result["content"]![0]!["text"]!.GetValue<string>();
        var isError = result["isError"]?.GetValue<bool>() ?? false;
        return (text, isError);
    }

    static string Row(string id, string status, string definition = "code-review", string title = "t", int round = 1, string roundStatus = "running", string startedAt = "2026-09-22T09:00:00Z") =>
        $$"""{"flow_run_id":"{{id}}","definition_id":"{{definition}}","status":"{{status}}","target_title":"{{title}}","round_number":{{round}},"round_status":"{{roundStatus}}","started_at":"{{startedAt}}"}""";

    static string Flows(params string[] rows) => $$"""{"flows":[{{string.Join(",", rows)}}]}""";

    static void GivenSessionFlows(WireMockServer server, string sessionId, string body) =>
        server.Given(Request.Create().WithPath("/api/flows").WithParam("requesting_session_id", sessionId).WithParam("state", "all").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(body));

    static void GivenFlow(WireMockServer server, string flowRunId, string status, string roundStatus = "findings") =>
        server.Given(Request.Create().WithPath($"/api/flows/{flowRunId}").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                  .WithBody($$"""{"flow_run_id":"{{flowRunId}}","status":"{{status}}","definition_id":"code-review","target_title":"t","round_count":1,"round_number":1,"round_status":"{{roundStatus}}","last_result_kind":"findings","last_result_text":"fix line 42"}"""));

    static int Gets(WireMockServer server, string path) => server.LogEntries.Count(e => e.RequestMessage.Path == path);

    static readonly TimeSpan PerGetTimeout = TimeSpan.FromSeconds(20);

    /// <summary>A server that never answers: virtual time passes the lookup's own timeout, whose
    /// source the clock then fires, and the request honours that cancellation.</summary>
    sealed class NeverAnsweringHandler(VirtualFlowRetryClock clock) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            clock.Advance(PerGetTimeout + TimeSpan.FromSeconds(1));
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("the lookup's timeout source did not fire");
        }
    }

    sealed class RefusingHandler : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("Connection refused");
    }

    [Test]
    public async Task A_lookup_that_times_out_is_an_actionable_error() {
        var clock = new VirtualFlowRetryClock();
        using var client = new HttpClient(new NeverAnsweringHandler(clock));

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("get_review_flow_status", new JsonObject()),
            client, "http://flows.test", cwd: "/tmp/cwd", repoRoot: null, repoInfo: null, clock: clock, requestingSessionId: SessionId);

        var (text, isError) = Unwrap(response);
        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("flow lookup");
        await Assert.That(text).Contains("timed out");
        await Assert.That(text).Contains("Pass the flow_run_id");
    }

    [Test]
    public async Task A_lookup_the_server_refuses_is_an_actionable_error() {
        using var client = new HttpClient(new RefusingHandler());

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("get_flow_status", new JsonObject()),
            client, "http://flows.test", cwd: "/tmp/cwd", repoRoot: null, repoInfo: null, requestingSessionId: SessionId);

        var (text, isError) = Unwrap(response);
        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("flow lookup");
        await Assert.That(text).Contains("Connection refused");
        await Assert.That(text).Contains("Pass the flow_run_id");
    }

    [Test]
    public async Task Omitted_flow_run_id_reads_the_sessions_only_open_flow() {
        using var server = WireMockServer.Start();
        GivenSessionFlows(server, SessionId, Flows(Row("flow-1", "waiting")));
        GivenFlow(server, "flow-1", "waiting");
        using var client = new HttpClient();

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("get_review_flow_status", new JsonObject()),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null, requestingSessionId: SessionId);

        var (text, isError) = Unwrap(response);
        await Assert.That(isError).IsFalse();
        await Assert.That(text).Contains("flow_run_id: flow-1");
        await Assert.That(text).Contains("fix line 42");
        await Assert.That(Gets(server, "/api/flows")).IsEqualTo(1);
        await Assert.That(Gets(server, "/api/flows/flow-1")).IsEqualTo(1);
    }

    [Test]
    public async Task Blank_flow_run_id_counts_as_omitted() {
        using var server = WireMockServer.Start();
        GivenSessionFlows(server, SessionId, Flows(Row("flow-1", "running")));
        GivenFlow(server, "flow-1", "running");
        using var client = new HttpClient();

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("get_flow_status", new JsonObject { ["flow_run_id"] = "  " }),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null, requestingSessionId: SessionId);

        var (text, isError) = Unwrap(response);
        await Assert.That(isError).IsFalse();
        await Assert.That(text).Contains("flow_run_id: flow-1");
    }

    [Test]
    public async Task Omitted_flow_run_id_with_wait_polls_the_resolved_flow() {
        using var server = WireMockServer.Start();
        GivenSessionFlows(server, SessionId, Flows(Row("flow-1", "running")));
        server.Given(Request.Create().WithPath("/api/flows/flow-1").UsingGet())
              .InScenario("resolved-wait").WillSetStateTo("second")
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                  .WithBody("""{"flow_run_id":"flow-1","status":"running","definition_id":"code-review","target_title":"t","round_count":1,"round_number":1,"round_status":"running"}"""));
        server.Given(Request.Create().WithPath("/api/flows/flow-1").UsingGet())
              .InScenario("resolved-wait").WhenStateIs("second")
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                  .WithBody("""{"flow_run_id":"flow-1","status":"waiting","definition_id":"code-review","target_title":"t","round_count":1,"round_number":1,"round_status":"findings","last_result_kind":"findings","last_result_text":"fix line 42"}"""));
        using var client = new HttpClient();

        var clock    = new VirtualFlowRetryClock();
        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("get_review_flow_status", new JsonObject { ["wait"] = true }),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null, clock: clock, requestingSessionId: SessionId);

        var (text, isError) = Unwrap(response);
        await Assert.That(isError).IsFalse();
        await Assert.That(text).Contains("fix line 42");
        await Assert.That(Gets(server, "/api/flows/flow-1")).IsEqualTo(2);
        await Assert.That(clock.Delays).IsEquivalentTo([PollInterval]);
    }

    [Test]
    public async Task Explicit_session_id_is_canonicalized_and_wins_over_the_requester_context() {
        using var server = WireMockServer.Start();
        GivenSessionFlows(server, "0a1b2c3d4e5f60718293a4b5c6d7e8f9", Flows(Row("flow-other", "running")));
        GivenFlow(server, "flow-other", "running");
        using var client = new HttpClient();

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!,
            ToolCallRequest("get_review_flow_status", new JsonObject { ["session_id"] = "0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9" }),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null, requestingSessionId: SessionId);

        var (text, isError) = Unwrap(response);
        await Assert.That(isError).IsFalse();
        await Assert.That(text).Contains("flow_run_id: flow-other");
        await Assert.That(server.LogEntries.Count(e => e.RequestMessage.Path == "/api/flows" && e.RequestMessage.Query!["requesting_session_id"].Contains(SessionId))).IsEqualTo(0);
    }

    [Test]
    public async Task An_open_flow_wins_over_a_newer_settled_one() {
        using var server = WireMockServer.Start();
        GivenSessionFlows(server, SessionId, Flows(Row("flow-new", "closed", startedAt: "2026-09-22T10:00:00Z"), Row("flow-old", "waiting", startedAt: "2026-09-22T09:00:00Z")));
        GivenFlow(server, "flow-old", "waiting");
        using var client = new HttpClient();

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("get_review_flow_status", new JsonObject()),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null, requestingSessionId: SessionId);

        var (text, isError) = Unwrap(response);
        await Assert.That(isError).IsFalse();
        await Assert.That(text).Contains("flow_run_id: flow-old");
        await Assert.That(Gets(server, "/api/flows/flow-new")).IsEqualTo(0);
    }

    [Test]
    public async Task Only_settled_flows_pick_the_newest_so_a_missed_ending_is_still_readable() {
        using var server = WireMockServer.Start();
        GivenSessionFlows(server, SessionId, Flows(Row("flow-9", "failed"), Row("flow-8", "closed")));
        GivenFlow(server, "flow-9", "failed", roundStatus: "failed");
        using var client = new HttpClient();

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("get_review_flow_status", new JsonObject()),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null, requestingSessionId: SessionId);

        var (text, isError) = Unwrap(response);
        await Assert.That(isError).IsFalse();
        await Assert.That(text).Contains("flow_run_id: flow-9");
        await Assert.That(text).Contains("status: failed");
    }

    [Test]
    public async Task Several_open_flows_list_the_candidates_instead_of_guessing() {
        using var server = WireMockServer.Start();
        GivenSessionFlows(server, SessionId, Flows(
            Row("flow-a", "waiting", definition: "code-review", title: "Add null check", round: 2, roundStatus: "findings"),
            Row("flow-b", "running", definition: "spec-review", title: "Design", round: 1, roundStatus: "running"),
            Row("flow-c", "closed")));
        using var client = new HttpClient();

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("get_review_flow_status", new JsonObject { ["wait"] = true }),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null, requestingSessionId: SessionId);

        var (text, isError) = Unwrap(response);
        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("2 open flows");
        await Assert.That(text).Contains("flow-a");
        await Assert.That(text).Contains("Add null check");
        await Assert.That(text).Contains("round 2 findings");
        await Assert.That(text).Contains("flow-b");
        await Assert.That(text).Contains("spec-review");
        await Assert.That(text).DoesNotContain("flow-c");
        await Assert.That(server.LogEntries.Count(e => e.RequestMessage.Path.StartsWith("/api/flows/", StringComparison.Ordinal))).IsEqualTo(0);
    }

    [Test]
    public async Task No_flow_for_the_session_is_a_clean_error() {
        using var server = WireMockServer.Start();
        GivenSessionFlows(server, SessionId, Flows());
        using var client = new HttpClient();

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("get_review_flow_status", new JsonObject()),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null, requestingSessionId: SessionId);

        var (text, isError) = Unwrap(response);
        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains($"no flow was started by session {SessionId}");
        await Assert.That(text).Contains("Pass the flow_run_id");
    }

    [Test]
    [Arguments("{}")]
    [Arguments("[]")]
    [Arguments("""{"flows":"none"}""")]
    public async Task A_list_without_a_flows_array_is_unreadable_not_empty(string body) {
        using var server = WireMockServer.Start();
        GivenSessionFlows(server, SessionId, body);
        using var client = new HttpClient();

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("get_review_flow_status", new JsonObject()),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null, requestingSessionId: SessionId);

        var (text, isError) = Unwrap(response);
        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("unreadable flow list");
        await Assert.That(text).DoesNotContain("no flow was started");
    }

    [Test]
    public async Task A_server_without_the_lookup_route_says_to_pass_the_id() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(404));
        using var client = new HttpClient();

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("get_flow_status", new JsonObject()),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null, requestingSessionId: SessionId);

        var (text, isError) = Unwrap(response);
        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("cannot look up flows by session");
        await Assert.That(text).Contains("Pass the flow_run_id");
    }

    const string Workspace = "/repo/a";

    /// <summary>Recorded an hour ago, past the grace a just-started run gets for a 404.</summary>
    void GivenRecordedRuns(params string[] newestLast) => GivenRecordedRunsAt(DateTimeOffset.UtcNow.AddHours(-1), newestLast);

    void GivenRecordedRunsAt(DateTimeOffset firstAt, params string[] newestLast) {
        var time   = new FakeTimeProvider(firstAt);
        var ledger = new FlowRunLedger(Config.Root, time);
        foreach (var flowRunId in newestLast) {
            ledger.Record(flowRunId, Workspace);
            time.Advance(TimeSpan.FromSeconds(1));
        }
    }

    Task<string> SessionlessStatusAsync(WireMockServer server, HttpClient client, JsonObject arguments, string? repoRoot = Workspace, FlowRetryClock? clock = null) =>
        Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("get_review_flow_status", arguments),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: repoRoot, repoInfo: null, clock: clock, requestingSessionId: null);

    [Test]
    public async Task No_session_and_no_recorded_run_is_a_clean_error_before_any_request() {
        using var server = WireMockServer.Start();
        using var client = new HttpClient();

        var (text, isError) = Unwrap(await SessionlessStatusAsync(server, client, new JsonObject()));

        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("no session id");
        await Assert.That(text).Contains("this workspace");
        await Assert.That(text).DoesNotContain(Workspace);
        await Assert.That(text).Contains("Pass the flow_run_id");
        await Assert.That(server.LogEntries.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Without_a_session_the_workspaces_recorded_open_run_is_read() {
        using var server = WireMockServer.Start();
        GivenRecordedRuns("flow-old", "flow-new");
        GivenFlow(server, "flow-old", "closed");
        GivenFlow(server, "flow-new", "waiting");
        using var client = new HttpClient();

        var (text, isError) = Unwrap(await SessionlessStatusAsync(server, client, new JsonObject()));

        await Assert.That(isError).IsFalse();
        await Assert.That(text).Contains("flow_run_id: flow-new");
        await Assert.That(text).Contains("fix line 42");
        await Assert.That(Gets(server, "/api/flows")).IsEqualTo(0);
    }

    [Test]
    public async Task Without_a_session_wait_polls_the_recorded_run() {
        using var server = WireMockServer.Start();
        GivenRecordedRuns("flow-1");
        server.Given(Request.Create().WithPath("/api/flows/flow-1").UsingGet())
              .InScenario("ledger-wait").WillSetStateTo("second")
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                  .WithBody("""{"flow_run_id":"flow-1","status":"running","definition_id":"code-review","target_title":"t","round_count":1,"round_number":1,"round_status":"running"}"""));
        server.Given(Request.Create().WithPath("/api/flows/flow-1").UsingGet())
              .InScenario("ledger-wait").WhenStateIs("second")
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                  .WithBody("""{"flow_run_id":"flow-1","status":"waiting","definition_id":"code-review","target_title":"t","round_count":1,"round_number":1,"round_status":"findings","last_result_kind":"findings","last_result_text":"fix line 42"}"""));
        using var client = new HttpClient();

        var (text, isError) = Unwrap(await SessionlessStatusAsync(server, client, new JsonObject { ["wait"] = true }, clock: new VirtualFlowRetryClock()));

        await Assert.That(isError).IsFalse();
        await Assert.That(text).Contains("fix line 42");
    }

    [Test]
    public async Task Another_workspaces_runs_are_not_candidates() {
        using var server = WireMockServer.Start();
        GivenRecordedRuns("flow-1");
        using var client = new HttpClient();

        var (text, isError) = Unwrap(await SessionlessStatusAsync(server, client, new JsonObject(), repoRoot: "/repo/b"));

        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("no flow started from this workspace");
        await Assert.That(server.LogEntries.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Without_a_session_several_open_recorded_runs_are_listed() {
        using var server = WireMockServer.Start();
        GivenRecordedRuns("flow-1", "flow-2");
        GivenFlow(server, "flow-1", "running");
        GivenFlow(server, "flow-2", "waiting");
        using var client = new HttpClient();

        var (text, isError) = Unwrap(await SessionlessStatusAsync(server, client, new JsonObject()));

        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("this workspace has 2 open flows");
        await Assert.That(text).DoesNotContain(Workspace);
        await Assert.That(text).Contains("flow-1");
        await Assert.That(text).Contains("flow-2");
    }

    [Test]
    public async Task A_recorded_run_the_server_no_longer_knows_is_skipped() {
        using var server = WireMockServer.Start();
        GivenRecordedRuns("flow-1", "flow-gone");
        server.Given(Request.Create().WithPath("/api/flows/flow-gone").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(404));
        GivenFlow(server, "flow-1", "closed");
        using var client = new HttpClient();

        var (text, isError) = Unwrap(await SessionlessStatusAsync(server, client, new JsonObject()));

        await Assert.That(isError).IsFalse();
        await Assert.That(text).Contains("flow_run_id: flow-1");
    }

    [Test]
    public async Task An_older_open_run_behind_many_newer_settled_ones_is_still_found() {
        using var server = WireMockServer.Start();
        var settled = Enumerable.Range(1, 8).Select(i => $"flow-settled-{i}").ToArray();
        GivenRecordedRuns(["flow-open", ..settled]);
        GivenFlow(server, "flow-open", "waiting");
        foreach (var flowRunId in settled) GivenFlow(server, flowRunId, "closed");
        using var client = new HttpClient();

        var (text, isError) = Unwrap(await SessionlessStatusAsync(server, client, new JsonObject()));

        await Assert.That(isError).IsFalse();
        await Assert.That(text).Contains("flow_run_id: flow-open");
    }

    [Test]
    public async Task A_just_recorded_run_the_server_cannot_read_yet_is_a_retry_not_a_skip() {
        using var server = WireMockServer.Start();
        GivenRecordedRunsAt(DateTimeOffset.UtcNow.AddHours(-1), "flow-older");
        GivenRecordedRunsAt(DateTimeOffset.UtcNow, "flow-just-started");
        GivenFlow(server, "flow-older", "waiting");
        server.Given(Request.Create().WithPath("/api/flows/flow-just-started").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(404));
        using var client = new HttpClient();

        var (text, isError) = Unwrap(await SessionlessStatusAsync(server, client, new JsonObject()));

        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("flow-just-started");
        await Assert.That(text).Contains("retry");
        await Assert.That(text).DoesNotContain("flow-older");
    }

    [Test]
    public async Task A_start_without_a_session_records_its_run_for_the_workspace() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                  """{"flow_run_id":"flow-started","status":"running","round_id":null,"round_number":null}"""));
        using var client = new HttpClient();

        await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("start_review_flow", StartArguments()),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: Workspace, repoInfo: null, requestingSessionId: null);

        await Assert.That(new FlowRunLedger(Config.Root, TimeProvider.System).Retained(Workspace).Select(e => e.FlowRunId))
            .IsEquivalentTo(["flow-started"]);
    }

    [Test]
    public async Task A_start_with_a_session_records_nothing() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows/review/start/v2").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                  """{"flow_run_id":"flow-started","status":"running","round_id":null,"round_number":null}"""));
        using var client = new HttpClient();

        await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("start_review_flow", StartArguments()),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: Workspace, repoInfo: null, requestingSessionId: SessionId);

        await Assert.That(new FlowRunLedger(Config.Root, TimeProvider.System).Retained(Workspace)).IsEmpty();
    }

    static JsonObject StartArguments() => new() {
        ["kind"]         = "code-review",
        ["target_kind"]  = "pr",
        ["target_ref"]   = "123",
        ["target_title"] = "some PR",
        ["context"]      = "some context"
    };

    [Test]
    public async Task A_non_string_flow_run_id_is_a_clean_error_before_any_request() {
        using var server = WireMockServer.Start();
        using var client = new HttpClient();

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("get_review_flow_status", new JsonObject { ["flow_run_id"] = 42 }),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null, requestingSessionId: SessionId);

        var (text, isError) = Unwrap(response);
        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("flow_run_id must be a string");
        await Assert.That(server.LogEntries.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Unauthorized_on_the_lookup_is_an_error_and_reads_no_flow() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/flows").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(401));
        using var client = new HttpClient();

        var response = await Server().HandleToolCallAsync(
            JsonNode.Parse("1")!, ToolCallRequest("get_review_flow_status", new JsonObject()),
            client, server.Url!, cwd: "/tmp/cwd", repoRoot: null, repoInfo: null, requestingSessionId: SessionId);

        var (_, isError) = Unwrap(response);
        await Assert.That(isError).IsTrue();
        await Assert.That(Gets(server, "/api/flows")).IsEqualTo(1);
        await Assert.That(server.LogEntries.Count).IsEqualTo(1);
    }
}
