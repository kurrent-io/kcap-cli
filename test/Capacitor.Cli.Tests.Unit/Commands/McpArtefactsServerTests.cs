using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class McpArtefactsServerTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string CapacitorSessionIdEnvVar = "KCAP_SESSION_ID";
    const string CodexThreadIdEnvVar      = "CODEX_THREAD_ID";

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
        var savedKap = Environment.GetEnvironmentVariable(CapacitorSessionIdEnvVar);
        var savedCdx = Environment.GetEnvironmentVariable(CodexThreadIdEnvVar);
        Environment.SetEnvironmentVariable(CapacitorSessionIdEnvVar, "ambientsess1");
        Environment.SetEnvironmentVariable(CodexThreadIdEnvVar, null);

        try {
            var body = McpArtefactsServer.BuildPublishBody(Args("""{"title":"Plan"}"""), "<p>x</p>");

            await Assert.That(body["sources"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray())
                        .IsEquivalentTo(new[] { "ambientsess1" });
        } finally {
            Environment.SetEnvironmentVariable(CapacitorSessionIdEnvVar, savedKap);
            Environment.SetEnvironmentVariable(CodexThreadIdEnvVar, savedCdx);
        }
    }

    [Test]
    [NotInParallel(SessionEnvVarMutation)]
    public async Task An_explicit_session_list_beats_the_ambient_one() {
        var savedKap = Environment.GetEnvironmentVariable(CapacitorSessionIdEnvVar);
        Environment.SetEnvironmentVariable(CapacitorSessionIdEnvVar, "ambientsess1");

        try {
            var body = McpArtefactsServer.BuildPublishBody(
                Args("""{"title":"Plan","session_ids":["1234abcd-56ef-78ab-90cd-1234567890ab"]}"""), "<p>x</p>");

            // Canonicalized the way the server files sessions, so a dashed GUID cites the session
            // the caller meant rather than silently citing nothing.
            await Assert.That(body["sources"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray())
                        .IsEquivalentTo(new[] { "1234abcd56ef78ab90cd1234567890ab" });
        } finally {
            Environment.SetEnvironmentVariable(CapacitorSessionIdEnvVar, savedKap);
        }
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
        // to nobody". Collapsing them would silently widen or narrow who can open the artefact.
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
        await Assert.That(() => McpArtefactsServer.BuildVisibilityBody(Args("""{"artefact_id":"a1"}""")))
                    .Throws<ArgumentException>();
    }

    // ── routing ───────────────────────────────────────────────────────────────────────

    [Test]
    public async Task Artefact_url_escapes_the_id() {
        var url = McpArtefactsServer.ArtefactUrl("https://kcap.test", Args("""{"artefact_id":"a b"}"""), "visibility");

        await Assert.That(url).IsEqualTo("https://kcap.test/api/artefacts/a%20b/visibility");
    }

    [Test]
    public async Task Artefact_url_refuses_an_id_that_would_walk_out_of_its_route() {
        foreach (var id in new[] { ".", "..", "a/b", "a\\b" }) {
            var args = new JsonObject { ["artefact_id"] = id };

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
    public async Task The_tool_list_is_exactly_these_six() {
        // Deliberate and worth keeping deliberate: an agent's context pays for every schema it
        // carries whether or not it ever publishes. Widening this is a decision, not a drive-by.
        var names = McpArtefactsServer.BuildToolsList().Select(t => t.Name).ToArray();

        await Assert.That(names).IsEquivalentTo(new[] {
            "publish_artefact", "await_artefact_responses", "get_artefact_results",
            "close_artefact_responses", "list_my_artefacts", "set_artefact_visibility"
        });
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

    [Test]
    public async Task The_instructions_tell_an_agent_the_page_cannot_reach_the_network() {
        // The one thing that fails silently: an external URL renders as nothing under the sandbox.
        await Assert.That(McpArtefactsServer.ServerInstructions).Contains("data URI");
    }

    static async Task WithoutAmbientSessionAsync(Func<Task> body) {
        var savedKap = Environment.GetEnvironmentVariable(CapacitorSessionIdEnvVar);
        var savedCdx = Environment.GetEnvironmentVariable(CodexThreadIdEnvVar);
        Environment.SetEnvironmentVariable(CapacitorSessionIdEnvVar, null);
        Environment.SetEnvironmentVariable(CodexThreadIdEnvVar, null);

        try {
            await body();
        } finally {
            Environment.SetEnvironmentVariable(CapacitorSessionIdEnvVar, savedKap);
            Environment.SetEnvironmentVariable(CodexThreadIdEnvVar, savedCdx);
        }
    }
}
