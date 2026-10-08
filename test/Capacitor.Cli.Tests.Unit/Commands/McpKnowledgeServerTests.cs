using System.Net;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.PrDetection;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// The knowledge tools' client half: arguments are the server's wire keys; reads default to the
/// working directory's repo and refuse to guess one; tri-state applicability survives (absent stays
/// absent, <c>[]</c> stays everywhere) and an org <c>target_scope_id</c> stays <c>""</c>; each tool
/// reaches its own route; and a coded refusal passes through verbatim as a tool error.
/// </summary>
public class McpKnowledgeServerTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    const string Doc = "0b9c2f4e-6a1d-4c0e-9d7b-3f2a1e5c8d90";

    McpKnowledgeServer Server(TimeProvider? time = null) =>
        new(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), new FixedCapacitorHttpClient(),
            NoTelemetry.Startup, router: new GitProviderRouter(), workdir: new WorkingDirectory(AppContext.BaseDirectory),
            time: time ?? TimeProvider.System);

    [Test]
    public async Task Reads_default_to_the_working_directorys_repo() {
        await Assert.That(McpKnowledgeServer.BuildListSkillsUrl("http://x", Args("{}"), "abc123"))
            .IsEqualTo("http://x/api/knowledge/skills?scope=repo&scope_id=abc123");
        await Assert.That(McpKnowledgeServer.BuildListFactsUrl("http://x", Args("""{"scope":"org"}"""), null))
            .IsEqualTo("http://x/api/knowledge/facts?scope=org&scope_id=");
        await Assert.That(McpKnowledgeServer.BuildListFactsUrl("http://x", Args("""{"scope":"project","scope_id":"payments"}"""), "abc123"))
            .IsEqualTo("http://x/api/knowledge/facts?scope=project&scope_id=payments");
    }

    [Test]
    public async Task A_repo_read_with_no_resolvable_repo_is_refused_rather_than_widened() {
        await Assert.That(() => McpKnowledgeServer.BuildListFactsUrl("http://x", Args("{}"), null)).Throws<ArgumentException>();
    }

    [Test]
    public async Task Filters_limits_and_cursors_pass_through_escaped() {
        var url = McpKnowledgeServer.BuildListFactsUrl("http://x",
            Args("""{"category":"safety","cluster_uid":"c-1","limit":20,"cursor":"a/b+c"}"""), "abc123");

        await Assert.That(url).IsEqualTo("http://x/api/knowledge/facts?scope=repo&scope_id=abc123&category=safety&cluster_uid=c-1&limit=20&cursor=a%2Fb%2Bc");
        await Assert.That(() => McpKnowledgeServer.BuildListSkillsUrl("http://x", Args("""{"limit":"ten"}"""), "abc123"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Search_needs_a_query() {
        await Assert.That(() => McpKnowledgeServer.BuildSearchFactsUrl("http://x", Args("{}"), "abc123")).Throws<ArgumentException>();
        await Assert.That(McpKnowledgeServer.BuildSearchFactsUrl("http://x", Args("""{"query":"lock order","limit":5}"""), "abc123"))
            .IsEqualTo("http://x/api/knowledge/facts/search?scope=repo&scope_id=abc123&query=lock%20order&limit=5");
    }

    [Test]
    public async Task Get_skill_needs_a_uuid_and_passes_its_flags() {
        await Assert.That(() => McpKnowledgeServer.BuildGetSkillUrl("http://x", Args("""{"doc_id":"nope"}"""), "abc123"))
            .Throws<ArgumentException>();
        await Assert.That(McpKnowledgeServer.BuildGetSkillUrl("http://x", Args($$"""{"doc_id":"{{Doc}}","include_members":false,"include_versions":true}"""), "abc123"))
            .IsEqualTo($"http://x/api/knowledge/skills/{Doc}?scope=repo&scope_id=abc123&include_members=false&include_versions=true");
    }

    [Test]
    public async Task Write_bodies_use_the_wire_keys_and_require_an_operation_id() {
        var edit = McpKnowledgeServer.BuildEditBody(Args("""{"body":"New body.","operation_id":"op-1","expected_doc_revision":7}"""));
        await Assert.That(edit.ToJsonString()).IsEqualTo("""{"body":"New body.","operation_id":"op-1","expected_doc_revision":7}""");

        var transition = McpKnowledgeServer.BuildTransitionBody(
            Args("""{"action":"approve","operation_id":"op-2","expected_doc_revision":4,"targets":["skill"]}"""));
        await Assert.That(transition["targets"]!.AsArray().Select(t => t!.GetValue<string>())).IsEquivalentTo(new[] { "skill" });
        await Assert.That(transition["expected_doc_revision"]!.GetValue<long>()).IsEqualTo(4);
        await Assert.That(transition["edited_body"]).IsNull();

        var adjust = McpKnowledgeServer.BuildAdjustMembersBody(Args("""{"action":"remove","cluster_uids":["u-1","u-2"],"operation_id":"op-3"}"""));
        await Assert.That(adjust["cluster_uids"]!.AsArray().Count).IsEqualTo(2);
        await Assert.That(adjust["expected_doc_revision"]).IsNull();

        await Assert.That(() => McpKnowledgeServer.BuildEditBody(Args("""{"body":"x","expected_doc_revision":1}"""))).Throws<ArgumentException>();
        await Assert.That(() => McpKnowledgeServer.BuildAdjustMembersBody(Args("""{"action":"add","cluster_uids":[],"operation_id":"op"}""")))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Sparse_transition_and_member_writes_omit_unsupplied_fields() {
        var transition = McpKnowledgeServer.BuildTransitionBody(Args("""
            {"action":"restore","operation_id":"op","expected_doc_revision":7}
            """));
        await Assert.That(transition.ToJsonString()).IsEqualTo(
            """{"action":"restore","operation_id":"op","expected_doc_revision":7}""");

        var members = McpKnowledgeServer.BuildAdjustMembersBody(Args("""
            {"action":"add","cluster_uids":["u"],"operation_id":"op"}
            """));
        await Assert.That(members.ToJsonString()).IsEqualTo(
            """{"action":"add","cluster_uids":["u"],"operation_id":"op"}""");
    }

    [Test]
    public async Task Body_and_transition_writes_require_the_revision_token() {
        await Assert.That(() => McpKnowledgeServer.BuildEditBody(Args("""{"body":"x","operation_id":"op"}""")))
            .Throws<ArgumentException>();
        await Assert.That(() => McpKnowledgeServer.BuildTransitionBody(Args("""{"action":"restore","operation_id":"op"}""")))
            .Throws<ArgumentException>();
        await Assert.That(() => McpKnowledgeServer.BuildEditBody(Args("""{"body":"x","operation_id":"op","expected_doc_revision":1.5}""")))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Curation_keeps_tri_state_axes_and_empty_ids() {
        var body = McpKnowledgeServer.BuildCurateBody(Args("""
            {"curation_key":{"source_repo_hash":"abc123","category":"safety","cluster_id":"c-1"},
             "operation_id":"op-1","audience_kind":"everyone",
             "status":"promoted","curated_text":"Rule.","target_kinds":["injection"],
             "applies_to_vendors":[],"target_scope_kind":"org","target_scope_id":"","preserve_decision":true}
            """));

        await Assert.That(body["curation_key"]!["source_repo_hash"]!.GetValue<string>()).IsEqualTo("abc123");
        await Assert.That(body["audience_id"]!.GetValue<string>()).IsEqualTo("");
        await Assert.That(body["applies_to_vendors"]!.AsArray().Count).IsEqualTo(0);
        await Assert.That(body["applies_to_session_kinds"]).IsNull();
        await Assert.That(body["target_scope_id"]!.GetValue<string>()).IsEqualTo("");
        await Assert.That(body["preserve_decision"]!.GetValue<bool>()).IsTrue();
        await Assert.That(body.ContainsKey("preserve_audience")).IsFalse();
        await Assert.That(() => McpKnowledgeServer.BuildCurateBody(Args("""{"operation_id":"op","audience_kind":"everyone"}""")))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Audience_only_curation_omits_unsupplied_decision_fields() {
        var body = McpKnowledgeServer.BuildCurateBody(Args("""
            {"curation_key":{"source_repo_hash":"r","category":"c","cluster_id":"k"},
             "operation_id":"o","audience_kind":"everyone"}
            """));

        await Assert.That(body.ToJsonString()).IsEqualTo(
            """{"curation_key":{"source_repo_hash":"r","category":"c","cluster_id":"k"},"operation_id":"o","audience_kind":"everyone","audience_id":""}""");
    }

    [Test]
    public async Task The_tool_list_is_the_eight_tools_and_every_array_declares_its_items() {
        var tools = McpKnowledgeServer.BuildToolsList();

        await Assert.That(tools.Select(t => t.Name)).IsEquivalentTo(new[] {
            "list_skills", "get_skill", "list_facts", "search_facts",
            "edit_skill_body", "transition_skill", "adjust_skill_members", "curate_fact_cluster",
        });
        foreach (var property in tools.SelectMany(t => t.InputSchema.Properties.Values).Where(p => p.Type == "array"))
            await Assert.That(property.Items).IsNotNull();
    }

    [Test]
    [Arguments("list_facts", "{}", """{"items":[{"fact_hash":"h","sources":{"source_kinds":["session_eval","work_item_eval"],"work_items":[{"work_item_id":"w","key":"#12"}]}}],"next_cursor":null}""")]
    [Arguments("search_facts", """{"query":"locks"}""", """{"hits":[{"fact":{"fact_hash":"h","sources":{"source_kinds":["work_item_eval"],"work_items":[{"work_item_id":"w","key":null}]}},"similarity":0.9}]}""")]
    public async Task A_facts_sources_pass_through_and_the_fact_tool_describes_them(string tool, string args, string body) {
        var response = JsonNode.Parse(await CallAsync(new RecordingHandler(body: body), tool, args))!;

        await Assert.That(response["result"]!["content"]![0]!["text"]!.GetValue<string>()).IsEqualTo(body);
        await Assert.That(McpKnowledgeServer.BuildToolsList().Single(t => t.Name == tool).Description).Contains("sources");
    }

    sealed class RecordingHandler(HttpStatusCode status = HttpStatusCode.OK, string body = """{"ok":true}""") : HttpMessageHandler {
        public List<(HttpMethod Method, string Path)> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            Calls.Add((request.Method, request.RequestUri!.AbsolutePath));
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    async Task<string> CallAsync(HttpMessageHandler handler, string tool, string argsJson, TimeProvider? time = null) {
        using var client = new HttpClient(handler);
        var request = new JsonObject { ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = JsonNode.Parse(argsJson) } };
        return await Server(time).HandleToolCallAsync(JsonValue.Create(1)!, request, client, "http://x", "abc123");
    }

    [Test]
    [Arguments("list_skills", "{}", "GET", "/api/knowledge/skills")]
    [Arguments("get_skill", """{"doc_id":"0b9c2f4e-6a1d-4c0e-9d7b-3f2a1e5c8d90"}""", "GET", "/api/knowledge/skills/0b9c2f4e-6a1d-4c0e-9d7b-3f2a1e5c8d90")]
    [Arguments("list_facts", "{}", "GET", "/api/knowledge/facts")]
    [Arguments("search_facts", """{"query":"locks"}""", "GET", "/api/knowledge/facts/search")]
    [Arguments("edit_skill_body", """{"doc_id":"0b9c2f4e-6a1d-4c0e-9d7b-3f2a1e5c8d90","body":"b","operation_id":"o","expected_doc_revision":1}""", "POST", "/api/knowledge/skills/0b9c2f4e-6a1d-4c0e-9d7b-3f2a1e5c8d90/body")]
    [Arguments("transition_skill", """{"doc_id":"0b9c2f4e-6a1d-4c0e-9d7b-3f2a1e5c8d90","action":"restore","operation_id":"o","expected_doc_revision":1}""", "POST", "/api/knowledge/skills/0b9c2f4e-6a1d-4c0e-9d7b-3f2a1e5c8d90/transition")]
    [Arguments("adjust_skill_members", """{"doc_id":"0b9c2f4e-6a1d-4c0e-9d7b-3f2a1e5c8d90","action":"add","cluster_uids":["u"],"operation_id":"o"}""", "POST", "/api/knowledge/skills/0b9c2f4e-6a1d-4c0e-9d7b-3f2a1e5c8d90/members")]
    [Arguments("curate_fact_cluster", """{"curation_key":{"source_repo_hash":"r","category":"c","cluster_id":"k"},"operation_id":"o","audience_kind":"everyone"}""", "POST", "/api/knowledge/facts/curate")]
    public async Task Each_tool_reaches_its_own_route(string tool, string args, string method, string path) {
        var handler = new RecordingHandler();

        var response = await CallAsync(handler, tool, args);

        await Assert.That(handler.Calls.Single().Method.Method).IsEqualTo(method);
        await Assert.That(handler.Calls.Single().Path).IsEqualTo(path);
        await Assert.That(response).DoesNotContain("\"isError\":true");
    }

    [Test]
    public async Task A_malformed_argument_is_a_tool_error_and_sends_nothing() {
        var handler = new RecordingHandler();

        var response = await CallAsync(handler, "edit_skill_body", $$"""{"doc_id":"{{Doc}}","body":"b","operation_id":"o"}""");

        await Assert.That(response).Contains("\"isError\":true");
        await Assert.That(response).Contains("expected_doc_revision");
        await Assert.That(handler.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_coded_refusal_passes_through_as_a_tool_error() {
        var handler = new RecordingHandler(HttpStatusCode.Conflict, """{"code":"doc_revision_mismatch","message":"m","doc_revision":7}""");

        var response = await CallAsync(handler, "edit_skill_body", $$"""{"doc_id":"{{Doc}}","body":"b","operation_id":"o","expected_doc_revision":3}""");

        await Assert.That(response).Contains("\"isError\":true");
        await Assert.That(response).Contains("doc_revision_mismatch");
        await Assert.That(response).Contains("409");
    }

    [Test]
    public async Task Unauthorized_reads_return_the_login_notice() {
        var response = await CallAsync(new RecordingHandler(HttpStatusCode.Unauthorized), "list_skills", "{}");

        await Assert.That(response).Contains("\"isError\":true");
        await Assert.That(response).Contains("kcap login");
    }

    [Test]
    public async Task Unknown_tools_return_a_tool_error_without_sending_a_request() {
        var handler = new RecordingHandler();

        var response = await CallAsync(handler, "missing_tool", "{}");

        await Assert.That(response).Contains("\"isError\":true");
        await Assert.That(response).Contains("Unknown tool: missing_tool");
        await Assert.That(handler.Calls.Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_no_content_success_says_so() {
        var handler = new RecordingHandler(HttpStatusCode.NoContent, "");

        var response = await CallAsync(handler, "curate_fact_cluster",
            """{"curation_key":{"source_repo_hash":"r","category":"c","cluster_id":"k"},"operation_id":"o","audience_kind":"everyone"}""");

        await Assert.That(response).Contains("HTTP 204");
        await Assert.That(response).DoesNotContain("\"isError\":true");
    }

    [Test]
    public async Task Every_curate_field_reaches_the_wire_under_its_own_key() {
        var body = McpKnowledgeServer.BuildCurateBody(Args("""
            {"curation_key":{"source_repo_hash":"abc123","category":"safety","cluster_id":"c-1"},
             "operation_id":"op-1","audience_kind":"team","audience_id":"t-1","curated_text":"Rule.",
             "target_kinds":["injection",""],"applies_to_vendors":["claude"],"applies_to_session_kinds":["hosted"],
             "applies_to_flow_roles":["reviewer"],"applies_to_platforms":["macos"],"status":"promoted","reason":"r",
             "target_scope_kind":"project","target_scope_id":"p-1","preserve_audience":true,"preserve_decision":true}
            """));

        await Assert.That(body.ToJsonString()).IsEqualTo(
            """{"curation_key":{"source_repo_hash":"abc123","category":"safety","cluster_id":"c-1"},"operation_id":"op-1","audience_kind":"team","audience_id":"t-1","curated_text":"Rule.","target_kinds":["injection",""],"applies_to_vendors":["claude"],"applies_to_session_kinds":["hosted"],"applies_to_flow_roles":["reviewer"],"applies_to_platforms":["macos"],"status":"promoted","reason":"r","target_scope_kind":"project","target_scope_id":"p-1","preserve_audience":true,"preserve_decision":true}""");
    }

    [Test]
    public async Task A_blank_status_reaches_the_server_and_a_blank_axis_value_does_not() {
        var body = McpKnowledgeServer.BuildCurateBody(Args("""
            {"curation_key":{"source_repo_hash":"r","category":"c","cluster_id":"k"},"operation_id":"o","audience_kind":"everyone","status":""}
            """));

        await Assert.That(body["status"]!.GetValue<string>()).IsEqualTo("");
        await Assert.That(() => McpKnowledgeServer.BuildCurateBody(Args("""
            {"curation_key":{"source_repo_hash":"r","category":"c","cluster_id":"k"},"operation_id":"o","audience_kind":"everyone","applies_to_vendors":[" "]}
            """))).Throws<ArgumentException>();
    }

    [Test]
    public async Task Every_transition_field_reaches_the_wire_under_its_own_key() {
        var body = McpKnowledgeServer.BuildTransitionBody(Args("""
            {"action":"revoke","operation_id":"op","expected_doc_revision":"12","targets":["skill"],"edited_body":"B",
             "reason":"why","member_disposition":"release"}
            """));

        await Assert.That(body.ToJsonString()).IsEqualTo(
            """{"action":"revoke","operation_id":"op","expected_doc_revision":12,"targets":["skill"],"edited_body":"B","reason":"why","member_disposition":"release"}""");
    }

    [Test]
    public async Task A_pending_approval_passes_through_as_a_success() {
        var handler = new RecordingHandler(HttpStatusCode.Accepted, """{"status":"pending_approval","message":"queued"}""");

        var response = await CallAsync(handler, "curate_fact_cluster",
            """{"curation_key":{"source_repo_hash":"r","category":"c","cluster_id":"k"},"operation_id":"o","audience_kind":"everyone","status":"promoted"}""");

        await Assert.That(response).Contains("pending_approval");
        await Assert.That(response).DoesNotContain("\"isError\":true");
    }

    [Test]
    public async Task A_refusal_with_no_body_says_so() {
        var handler = new RecordingHandler(HttpStatusCode.RequestEntityTooLarge, "");

        var response = await CallAsync(handler, "edit_skill_body", $$"""{"doc_id":"{{Doc}}","body":"b","operation_id":"o","expected_doc_revision":3}""");

        await Assert.That(response).Contains("\"isError\":true");
        await Assert.That(response).Contains("Error: HTTP 413 (no body)");
    }

    sealed class HangingHandler : HttpMessageHandler {
        public TaskCompletionSource Sent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            Sent.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    [Test]
    public async Task A_server_that_never_answers_becomes_a_tool_error_when_the_budget_runs_out() {
        var time    = new FakeTimeProvider();
        var handler = new HangingHandler();

        var call = CallAsync(handler, "list_skills", "{}", time);
        await handler.Sent.Task;
        time.Advance(McpKnowledgeServer.RequestBudget);
        var response = await call;

        await Assert.That(response).Contains("\"isError\":true");
        await Assert.That(response).Contains("did not answer in time");
    }
}
