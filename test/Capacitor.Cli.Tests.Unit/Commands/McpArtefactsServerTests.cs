using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Mcp;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class McpArtefactsServerTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string CapacitorSessionIdEnvVar = "KCAP_SESSION_ID";
    const string CodexThreadIdEnvVar      = "CODEX_THREAD_ID";
    const string ClaudeSessionIdEnvVar    = "CLAUDE_CODE_SESSION_ID";
    const string GrokSessionIdEnvVar      = "GROK_SESSION_ID";

    // Shares ArgParsingTests' NotInParallel key: both suites mutate the same process-global
    // KCAP_SESSION_ID / CODEX_THREAD_ID env vars, so tests in either must not interleave.
    const string SessionEnvVarMutation = "SessionEnvVarMutation";

    static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    // ── the page itself ───────────────────────────────────────────────────────────────

    [Test]
    public async Task Inline_html_is_published_as_given() {
        var html = McpArtefactsServer.ResolveHtml(Args("""{"html":"<h1>hi</h1>"}"""));

        await Assert.That(html).IsEqualTo("<h1>hi</h1>");
    }

    [Test]
    public async Task A_path_is_read_from_disk() {
        var file = Path.Combine(Config.Directory, "page.html");
        await File.WriteAllTextAsync(file, "<p>from disk</p>");

        var html = McpArtefactsServer.ResolveHtml(Args($$"""{"path":{{JsonValue.Create(file)!.ToJsonString()}}}"""));

        await Assert.That(html).IsEqualTo("<p>from disk</p>");
    }

    [Test]
    public async Task Html_and_path_together_are_refused_rather_than_ranked() {
        // Picking one silently would publish something the caller did not mean half the time.
        await Assert.That(() => McpArtefactsServer.ResolveHtml(Args("""{"html":"<p>a</p>","path":"b.html"}""")))
                    .Throws<ArgumentException>();
    }

    [Test]
    public async Task Neither_html_nor_path_is_refused() {
        await Assert.That(() => McpArtefactsServer.ResolveHtml(Args("""{"title":"t"}""")))
                    .Throws<ArgumentException>();
    }

    [Test]
    public async Task A_path_that_does_not_exist_is_refused() {
        var missing = Path.Combine(Config.Directory, "nope.html");

        await Assert.That(() => McpArtefactsServer.ResolveHtml(Args($$"""{"path":{{JsonValue.Create(missing)!.ToJsonString()}}}""")))
                    .Throws<ArgumentException>();
    }

    [Test]
    public async Task An_empty_file_is_refused() {
        var file = Path.Combine(Config.Directory, "empty.html");
        await File.WriteAllTextAsync(file, "   \n");

        await Assert.That(() => McpArtefactsServer.ResolveHtml(Args($$"""{"path":{{JsonValue.Create(file)!.ToJsonString()}}}""")))
                    .Throws<ArgumentException>();
    }

    // ── the publish body ──────────────────────────────────────────────────────────────

    [Test]
    [NotInParallel(SessionEnvVarMutation)]
    public async Task Publish_body_carries_only_what_was_supplied() {
        await WithoutAmbientSessionAsync(async () => {
            var body = McpArtefactsServer.BuildPublishBody(Args("""{"title":"Plan"}"""), "<p>x</p>");

            await Assert.That(body["title"]!.GetValue<string>()).IsEqualTo("Plan");
            await Assert.That(body["html"]!.GetValue<string>()).IsEqualTo("<p>x</p>");
            await Assert.That(body.ContainsKey("description")).IsFalse();
            await Assert.That(body.ContainsKey("visibility")).IsFalse();
            await Assert.That(body.ContainsKey("grants")).IsFalse();
            await Assert.That(body.ContainsKey("sources")).IsFalse();
        });
    }

    [Test]
    [NotInParallel(SessionEnvVarMutation)]
    public async Task Publish_body_cites_the_ambient_session_without_being_asked() {
        await WithoutAmbientSessionAsync(async () => {
            using var kap = new EnvScope(CapacitorSessionIdEnvVar, "ambientsess1");

            var body = McpArtefactsServer.BuildPublishBody(Args("""{"title":"Plan"}"""), "<p>x</p>");

            await Assert.That(body["sources"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray())
                        .IsEquivalentTo(new[] { "ambientsess1" });
        });
    }

    [Test]
    public async Task Publish_body_cites_claude_code_session_id_in_mcp_stdio_env() {
        var body = McpArtefactsServer.BuildPublishBody(
            Args("""{"title":"Plan"}"""),
            "<p>x</p>",
            name => name == ClaudeSessionIdEnvVar ? "69849952-6dcc-484c-81cc-ccf504b1a807" : null);

        await Assert.That(body["sources"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray())
                    .IsEquivalentTo(new[] { "698499526dcc484c81ccccf504b1a807" });
    }

    [Test]
    [NotInParallel(SessionEnvVarMutation)]
    public async Task An_explicit_session_list_beats_the_ambient_one() {
        using var kap = new EnvScope(CapacitorSessionIdEnvVar, "ambientsess1");

        var body = McpArtefactsServer.BuildPublishBody(
            Args("""{"title":"Plan","session_ids":["1234abcd-56ef-78ab-90cd-1234567890ab"]}"""), "<p>x</p>");

        // Canonicalized the way the server files sessions, so a dashed GUID cites the session
        // the caller meant rather than silently citing nothing.
        await Assert.That(body["sources"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray())
                    .IsEquivalentTo(new[] { "1234abcd56ef78ab90cd1234567890ab" });
    }

    [Test]
    public async Task Publish_body_requires_a_title() {
        await Assert.That(() => McpArtefactsServer.BuildPublishBody(Args("""{"visibility":"org"}"""), "<p>x</p>"))
                    .Throws<ArgumentException>();
    }

    // ── the audience ──────────────────────────────────────────────────────────────────

    [Test]
    public async Task An_absent_grants_key_is_not_an_empty_audience() {
        // Absence means "leave the audience to the visibility tier"; an empty array means "granted
        // to nobody". Collapsing them would silently widen or narrow who can open the page.
        await Assert.That(McpArtefactsServer.ReadGrants(Args("""{"visibility":"org"}"""))).IsNull();

        var empty = McpArtefactsServer.ReadGrants(Args("""{"grants":[]}"""));

        await Assert.That(empty).IsNotNull();
        await Assert.That(empty!.Count).IsEqualTo(0);
    }

    [Test]
    public async Task A_grant_without_a_name_falls_back_to_its_id() {
        var grants = McpArtefactsServer.ReadGrants(Args("""{"grants":[{"grant_type":"team","grantee_id":"platform"}]}"""))!;

        await Assert.That(grants.Count).IsEqualTo(1);
        await Assert.That(grants[0]!["grant_type"]!.GetValue<string>()).IsEqualTo("team");
        await Assert.That(grants[0]!["grantee_id"]!.GetValue<string>()).IsEqualTo("platform");
        await Assert.That(grants[0]!["grantee_name"]!.GetValue<string>()).IsEqualTo("platform");
    }

    [Test]
    public async Task A_supplied_grant_name_is_kept() {
        var grants = McpArtefactsServer.ReadGrants(
            Args("""{"grants":[{"grant_type":"user","grantee_id":"github:7","grantee_name":"Ada"}]}"""))!;

        await Assert.That(grants[0]!["grantee_name"]!.GetValue<string>()).IsEqualTo("Ada");
    }

    [Test]
    public async Task A_malformed_grant_fails_rather_than_being_dropped() {
        // Dropping it would narrow the audience without saying so.
        await Assert.That(() => McpArtefactsServer.ReadGrants(Args("""{"grants":["team:platform"]}""")))
                    .Throws<ArgumentException>();

        await Assert.That(() => McpArtefactsServer.ReadGrants(Args("""{"grants":[{"grant_type":"team"}]}""")))
                    .Throws<ArgumentException>();

        await Assert.That(() => McpArtefactsServer.ReadGrants(Args("""{"grants":null}""")))
                    .Throws<ArgumentException>();
    }

    [Test]
    public async Task Visibility_body_requires_a_visibility() {
        await Assert.That(() => McpArtefactsServer.BuildVisibilityBody(Args("""{"page_id":"a1"}""")))
                    .Throws<ArgumentException>();
    }

    // ── routing ───────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Artefact_url_escapes_the_id() {
        var url = McpArtefactsServer.ArtefactUrl("https://kcap.test", Args("""{"page_id":"a b"}"""), "visibility");

        await Assert.That(url).IsEqualTo("https://kcap.test/api/artefacts/a%20b/visibility");
    }

    [Test]
    public async Task Artefact_url_refuses_an_id_that_would_walk_out_of_its_route() {
        foreach (var id in new[] { ".", "..", "a/b", "a\\b" }) {
            var args = new JsonObject { ["page_id"] = id };

            await Assert.That(() => McpArtefactsServer.ArtefactUrl("https://kcap.test", args, "visibility"))
                        .Throws<ArgumentException>();
        }
    }

    [Test]
    public async Task Artefact_url_requires_an_id() {
        await Assert.That(() => McpArtefactsServer.ArtefactUrl("https://kcap.test", new JsonObject(), "visibility"))
                    .Throws<ArgumentException>();
    }

    // ── the advertised surface ────────────────────────────────────────────────────────

    [Test]
    public async Task The_tool_list_is_exactly_these_eight() {
        // Deliberate and worth keeping deliberate: an agent's context pays for every schema it
        // carries whether or not it ever publishes. Widening this is a decision, not a drive-by.
        var names = McpArtefactsServer.BuildToolsList().Select(t => t.Name).ToArray();

        await Assert.That(names).IsEquivalentTo(new[] {
            "publish_page", "await_page_responses", "get_page_results",
            "close_page_responses", "list_my_pages", "search_pages", "get_page", "set_page_visibility"
        });
    }

    [Test]
    public async Task Search_pages_requires_a_query_and_only_reads() {
        var tool = McpArtefactsServer.BuildToolsList().Single(t => t.Name == "search_pages");

        await Assert.That(tool.InputSchema.Required).IsEquivalentTo(new[] { "query" });
        await Assert.That(tool.InputSchema.Properties["limit"].Type).IsEqualTo("integer");
        await Assert.That(tool.Annotations).IsEqualTo(McpToolAnnotations.Read);
    }

    [Test]
    public async Task Server_instructions_point_at_search_pages() =>
        await Assert.That(McpArtefactsServer.ServerInstructions).Contains("search_pages");

    // ── reading a page ────────────────────────────────────────────────────────────────

    McpArtefactsServer Server() =>
        new(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root),
            new FixedCapacitorHttpClient(), NoTelemetry.Startup, TimeProvider.System);

    static async Task<(bool IsError, string Text)> CallAsync(McpArtefactsServer server, string baseUrl, JsonObject arguments,
                                                             string tool = "get_page") {
        using var client = new HttpClient();

        var request = new JsonObject { ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments } };
        var result  = JsonNode.Parse(await server.HandleToolCallAsync(JsonNode.Parse("1")!, request, client, baseUrl))!["result"]!;

        return (result["isError"]?.GetValue<bool>() ?? false, result["content"]![0]!["text"]!.GetValue<string>());
    }

    const string SharedPageJson = """
        {"artefact":{"artefact_id":"a1","title":"Plan","owner_user_id":"u2","visibility":"org","latest_version":3,
         "updated_at":"2026-09-16T10:00:00+00:00","is_owner":false,"url":"https://kcap.test/pages/a1"},"grants":[]}
        """;

    [Test]
    public async Task A_shared_page_named_by_its_link_reads_as_its_text_at_the_version_its_record_names() {
        using var stub = WireMockServer.Start();
        stub.Given(Request.Create().WithPath("/api/artefacts/a1").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(SharedPageJson));
        stub.Given(Request.Create().WithPath("/artefacts/a1/content").WithParam("v", "3").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("<h1>Ship it</h1><script>steal()</script>"));

        var (isError, text) = await CallAsync(Server(), stub.Url!, Args("""{"page_id":"https://kcap.test/pages/a1"}"""));

        await Assert.That(isError).IsFalse();

        var read = JsonNode.Parse(text)!.AsObject();
        await Assert.That(read["page"]!["page_id"]!.GetValue<string>()).IsEqualTo("a1");
        await Assert.That(read["version"]!.GetValue<int>()).IsEqualTo(3);
        await Assert.That(read["content"]!.GetValue<string>()).IsEqualTo("Ship it");

        // Read before the content, so the agent meets the warning first.
        var keys = read.Select(p => p.Key).ToList();
        await Assert.That(keys.IndexOf("notice")).IsLessThan(keys.IndexOf("content"));
    }

    [Test]
    public async Task A_page_the_caller_may_not_open_is_not_found_and_its_content_is_never_asked_for() {
        using var stub = WireMockServer.Start();
        stub.Given(Request.Create().WithPath("/api/artefacts/a1").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        var (isError, text) = await CallAsync(Server(), stub.Url!, Args("""{"page_id":"a1"}"""));

        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("no such page");
        await Assert.That(stub.LogEntries.Any(e => e.RequestMessage.Path.EndsWith("/content", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    [Arguments("""{"page_id":"a/b"}""")]
    [Arguments("""{"page_id":"https://kcap.test/sessions/s1"}""")]
    [Arguments("""{"page_id":"a1","format":"markdown"}""")]
    [Arguments("""{"page_id":"a1","max_chars":0}""")]
    [Arguments("""{"page_id":"a1","version":0}""")]
    public async Task A_read_request_that_names_no_page_or_an_unusable_shape_is_refused(string args) =>
        await Assert.That(() => McpArtefactsServer.ReadPageRequest.From(Args(args))).Throws<ArgumentException>();

    [Test]
    public async Task A_page_its_scripts_draw_comes_back_as_html_and_says_why() {
        const string html = "<div id=\"root\"></div><script>root.textContent = 'drawn'</script>";
        var request = McpArtefactsServer.ReadPageRequest.From(Args("""{"page_id":"a1"}"""));

        var read = McpArtefactsServer.BuildPageRead(new JsonObject(), 1, html, request);

        await Assert.That(read["format"]!.GetValue<string>()).IsEqualTo("html");
        await Assert.That(read["content"]!.GetValue<string>()).IsEqualTo(html);
        await Assert.That(read["format_note"]!.GetValue<string>()).IsEqualTo(McpArtefactsServer.ScriptDrawnNote);
    }

    [Test]
    public async Task A_long_page_is_cut_and_says_how_long_it_is() {
        var request = McpArtefactsServer.ReadPageRequest.From(Args("""{"page_id":"a1","format":"html","max_chars":4}"""));

        var read = McpArtefactsServer.BuildPageRead(new JsonObject(), 1, "<p>abcdef</p>", request);

        await Assert.That(read["content"]!.GetValue<string>()).IsEqualTo("<p>a");
        await Assert.That(read["truncated"]!.GetValue<bool>()).IsTrue();
        await Assert.That(read["content_length"]!.GetValue<int>()).IsEqualTo(13);
    }

    [Test]
    public async Task Closing_defaults_to_closed_and_reopening_has_to_be_asked_for() {
        await Assert.That(McpArtefactsServer.BuildCloseBody(Args("""{"version":2}""")).ContainsKey("closed")).IsFalse();

        await Assert.That(McpArtefactsServer.BuildCloseBody(Args("""{"version":2,"closed":false}"""))["closed"]!
                          .GetValue<bool>()).IsFalse();
    }

    [Test]
    public async Task Closing_needs_the_version_it_is_closing() =>
        await Assert.That(() => McpArtefactsServer.BuildCloseBody(Args("""{}"""))).Throws<ArgumentException>();

    [Test]
    public async Task A_declared_schema_is_forwarded_whole_rather_than_reshaped() {
        // The server owns every rule about what a schema may declare; a second interpretation here
        // would be a second place for the two to drift.
        var body = McpArtefactsServer.BuildPublishBody(
            Args("""{"title":"Plan","response_schema":{"fields":[{"id":"ok","type":"choice","options":["y","n"]}],"results_mode":"aggregate"}}"""),
            "<p>x</p>");

        var schema = body["response_schema"]!.AsObject();

        await Assert.That(schema["results_mode"]!.GetValue<string>()).IsEqualTo("aggregate");
        await Assert.That(schema["fields"]!.AsArray().Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_response_schema_that_is_not_an_object_is_refused() =>
        await Assert.That(() => McpArtefactsServer.BuildPublishBody(
                              Args("""{"title":"Plan","response_schema":"fields"}"""), "<p>x</p>"))
                    .Throws<ArgumentException>();

    // ── a revision ────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Without_update_id_a_publish_creates_an_artefact() {
        var (url, _) = McpArtefactsServer.BuildPublishRequest("https://kcap.test", Args("""{"title":"Plan"}"""), "<p>x</p>");

        await Assert.That(url).IsEqualTo("https://kcap.test/api/artefacts");
    }

    [Test]
    public async Task With_update_id_a_publish_is_a_version_of_that_artefact() {
        var (url, body) = McpArtefactsServer.BuildPublishRequest(
            "https://kcap.test", Args("""{"title":"Plan","update_id":"a b"}"""), "<p>x</p>");

        await Assert.That(url).IsEqualTo("https://kcap.test/api/artefacts/a%20b/versions");
        await Assert.That(body["html"]!.GetValue<string>()).IsEqualTo("<p>x</p>");
    }

    [Test]
    [Arguments("""{"title":"Plan","update_id":""}""")]
    [Arguments("""{"title":"Plan","update_id":"  "}""")]
    [Arguments("""{"title":"Plan","update_id":7}""")]
    public async Task An_update_id_that_names_nothing_is_refused_rather_than_creating_an_artefact(string args) =>
        await Assert.That(() => McpArtefactsServer.BuildPublishRequest("https://kcap.test", Args(args), "<p>x</p>"))
                    .Throws<ArgumentException>();

    [Test]
    public async Task A_revision_carries_its_own_response_schema() {
        // The server keeps a schema per version, so one dropped here leaves the revision unanswerable.
        var (_, body) = McpArtefactsServer.BuildPublishRequest(
            "https://kcap.test",
            Args("""{"title":"Plan","update_id":"a1","response_schema":{"fields":[{"id":"ok","type":"text"}]}}"""),
            "<p>x</p>");

        await Assert.That(body["response_schema"]!["fields"]!.AsArray().Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("visibility", "\"org\"")]
    [Arguments("grants", "[]")]
    public async Task A_revision_refuses_an_audience_it_would_not_apply(string key, string value) =>
        await Assert.That(() => McpArtefactsServer.BuildPublishRequest(
                              "https://kcap.test", Args($$"""{"title":"Plan","update_id":"a1","{{key}}":{{value}}}"""), "<p>x</p>"))
                    .Throws<ArgumentException>();

    // ── the long poll ─────────────────────────────────────────────────────────────────

    [Test]
    public async Task A_wait_defaults_to_one_respondent_on_the_latest_version() {
        var url = McpArtefactsServer.WaitUrl("https://kcap.test", Args("""{"page_id":"a1"}"""));

        await Assert.That(url).IsEqualTo("https://kcap.test/api/artefacts/a1/responses/wait?min_respondents=1");
    }

    [Test]
    public async Task A_wait_on_a_named_version_still_reaches_the_wait_route() {
        var url = McpArtefactsServer.WaitUrl(
            "https://kcap.test", Args("""{"page_id":"a1","version":2,"min_respondents":3,"timeout_s":60}"""));

        await Assert.That(url).IsEqualTo(
            "https://kcap.test/api/artefacts/a1/responses/wait?min_respondents=3&version=2&timeout_s=60");
    }

    [Test]
    public async Task The_clients_own_timeout_cannot_end_a_wait_before_the_server_does() {
        // HttpClient's 100-second default would abort a wait the server is still holding; each call
        // carries its own budget instead.
        using var client = new HttpClient();

        McpArtefactsServer.LiftClientTimeout(client);

        await Assert.That(client.Timeout).IsEqualTo(Timeout.InfiniteTimeSpan);
        await Assert.That(McpArtefactsServer.ClientWaitBudget).IsGreaterThan(McpArtefactsServer.RequestBudget);
    }

    [Test]
    public async Task The_instructions_tell_an_agent_the_page_cannot_reach_the_network() {
        // The one thing that fails silently: an external URL renders as nothing under the sandbox.
        await Assert.That(McpArtefactsServer.ServerInstructions).Contains("data URI");
    }

    // ── a request for "an artifact" ───────────────────────────────────────────────────

    public static IEnumerable<Func<(string Surface, string Text)>> ArtifactSteeringSurfaces() {
        yield return () => ("server instructions", McpArtefactsServer.ServerInstructions);
        yield return () => ("publish_page description",
                            McpArtefactsServer.BuildToolsList().Single(t => t.Name == "publish_page").Description);
        yield return () => ("canonical kcap-pages description",
                            KcapMcpServers.All.Single(s => s.Name == "kcap-pages").Description!);
        yield return () => ("bundled .mcp.json description",
                            JsonNode.Parse(File.ReadAllText(Path.Combine(RepoTree.KcapDir(), ".mcp.json")))!
                                ["mcpServers"]!["kcap-pages"]!["description"]!.GetValue<string>());
        yield return () => ("pages skill description",
                            File.ReadAllText(Path.Combine(RepoTree.SkillsSource(), "pages", "SKILL.md")));
    }

    /// <summary>Asked for "an artifact", a harness with kcap installed makes a page. Every surface
    /// the harnesses read has to claim that word: a harness whose own artifact tool is described
    /// at length takes the request for itself whenever kcap's tools say nothing about it.</summary>
    [Test]
    [MethodDataSource(nameof(ArtifactSteeringSurfaces))]
    public async Task Every_steering_surface_claims_a_request_for_an_artifact(string surface, string text) =>
        await Assert.That(text.Contains("artifact", StringComparison.OrdinalIgnoreCase)).IsTrue().Because(surface);

    /// <summary>One JSON block, not two content items: harnesses join a result's blocks into one
    /// string, and the desktop app's page card parses that string as a single JSON document.</summary>
    [Test]
    public async Task A_published_page_comes_back_as_one_json_block_carrying_the_follow_up() {
        var response = JsonNode.Parse(McpArtefactsServer.BuildPublishResult(
            JsonValue.Create(7)!, """{"artefact":{"artefact_id":"a1","url":"https://kcap.test/pages/a1"}}"""))!;

        var content = response["result"]!["content"]!.AsArray();

        await Assert.That(content.Count).IsEqualTo(1);

        var result = JsonNode.Parse(content[0]!["text"]!.GetValue<string>())!.AsObject();

        await Assert.That(result["page"]!["page_id"]!.GetValue<string>()).IsEqualTo("a1");
        await Assert.That(result["follow_up"]!.GetValue<string>()).IsEqualTo(McpArtefactsServer.PublishFollowUp);
        await Assert.That(response["result"]!["isError"]).IsNull();
    }

    /// <summary>A body that is not JSON comes back as it is, the way the renamer treats it; the
    /// follow-up has nowhere to go and a thrown parse would fail a publish the server accepted.</summary>
    [Test]
    public async Task A_publish_body_that_is_not_json_comes_back_untouched() {
        var response = JsonNode.Parse(McpArtefactsServer.BuildPublishResult(JsonValue.Create(7)!, "accepted"))!;

        var content = response["result"]!["content"]!.AsArray();

        await Assert.That(content.Count).IsEqualTo(1);
        await Assert.That(content[0]!["text"]!.GetValue<string>()).IsEqualTo("accepted");
    }

    /// <summary>The link always reaches the user in the reply; the browser tool is an addition
    /// where the harness has one, never a replacement, and never a system browser — a hosted
    /// agent's host is not the user's machine.</summary>
    [Test]
    public async Task The_follow_up_asks_for_the_link_in_the_reply_and_the_harness_browser_tool() {
        await Assert.That(McpArtefactsServer.PublishFollowUp).Contains("link");
        await Assert.That(McpArtefactsServer.PublishFollowUp).Contains("browser tool");
    }

    // ── searching pages ───────────────────────────────────────────────────────────────

    static WireMockServer SearchStub(string body) {
        var stub = WireMockServer.Start();
        stub.Given(Request.Create().WithPath("/api/search").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(body));

        return stub;
    }

    [Test]
    public async Task Search_pages_returns_only_pages_with_page_ids() {
        using var stub = SearchStub("""
            {"sessions":null,"transcripts":null,"took_ms":4,"work_items":null,
             "pages":[{"artefact_id":"p1","title":"Ferry timetable","owner_user_id":"github:42","owner_display_name":"Ada",
                       "visibility":"org","latest_version":2,"updated_at":"2026-09-16T10:00:00+00:00","is_owner":false,
                       "url":"https://kcap.test/pages/p1","excerpt":"harbour departures","matched_in":"body"}]}
            """);

        var (isError, text) = await CallAsync(Server(), stub.Url!, Args("""{"query":"ferry & timetable#"}"""), "search_pages");

        await Assert.That(isError).IsFalse();

        var result = JsonNode.Parse(text)!.AsObject();
        await Assert.That(result.Select(p => p.Key)).IsEquivalentTo(new[] { "pages" });
        await Assert.That(result["pages"]![0]!["page_id"]!.GetValue<string>()).IsEqualTo("p1");
        await Assert.That(result["pages"]![0]!.AsObject().ContainsKey("artefact_id")).IsFalse();

        var query = stub.LogEntries.Single().RequestMessage.Query!;
        await Assert.That(query["q"].Single()).IsEqualTo("ferry & timetable#");
        await Assert.That(query["mode"].Single()).IsEqualTo("pages");
        await Assert.That(query["limit"].Single()).IsEqualTo("10");
    }

    /// <summary>An older server answers this mode as a session search with no pages lane. An agent
    /// told "nothing matched" there would conclude the page does not exist.</summary>
    [Test]
    [Arguments("""{"sessions":[],"transcripts":null,"took_ms":1}""")]
    [Arguments("""{"sessions":null,"transcripts":null,"took_ms":1,"pages":null}""")]
    public async Task Search_pages_reports_an_older_server(string body) {
        using var stub = SearchStub(body);

        var (isError, text) = await CallAsync(Server(), stub.Url!, Args("""{"query":"ferry"}"""), "search_pages");

        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains("does not support page search");
    }

    [Test]
    public async Task Search_pages_with_an_empty_answer_succeeds() {
        using var stub = SearchStub("""{"sessions":null,"transcripts":null,"took_ms":0,"pages":[]}""");

        var (isError, text) = await CallAsync(Server(), stub.Url!, Args("""{"query":"ferry","limit":80}"""), "search_pages");

        await Assert.That(isError).IsFalse();
        await Assert.That(JsonNode.Parse(text)!.ToJsonString()).IsEqualTo("""{"pages":[]}""");
        await Assert.That(stub.LogEntries.Single().RequestMessage.Query!["limit"].Single()).IsEqualTo("50");
    }

    [Test]
    [Arguments("""{"query":5}""", "'query'")]
    [Arguments("""{"query":"ferry","limit":"many"}""", "'limit'")]
    [Arguments("""{"query":"ferry","limit":5.5}""", "'limit'")]
    public async Task Search_pages_with_a_mistyped_argument_names_it_without_a_request(string arguments, string named) {
        using var stub = SearchStub("""{"pages":[]}""");

        var (isError, text) = await CallAsync(Server(), stub.Url!, Args(arguments), "search_pages");

        await Assert.That(isError).IsTrue();
        await Assert.That(text).Contains(named);
        await Assert.That(stub.LogEntries).IsEmpty();
    }

    [Test]
    [Arguments("{}")]
    [Arguments("""{"query":"   "}""")]
    public async Task Search_pages_without_a_query_errors_without_a_request(string arguments) {
        using var stub = SearchStub("""{"pages":[]}""");

        var (isError, _) = await CallAsync(Server(), stub.Url!, Args(arguments), "search_pages");

        await Assert.That(isError).IsTrue();
        await Assert.That(stub.LogEntries).IsEmpty();
    }

    static async Task WithoutAmbientSessionAsync(Func<Task> body) {
        using var kap = new EnvScope(CapacitorSessionIdEnvVar, null);
        using var cdx = new EnvScope(CodexThreadIdEnvVar, null);
        using var cld = new EnvScope(ClaudeSessionIdEnvVar, null);
        using var grk = new EnvScope(GrokSessionIdEnvVar, null);

        await body();
    }
}
