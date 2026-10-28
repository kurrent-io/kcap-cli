using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class McpMemoryServerTests {
    static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    [Test]
    public async Task Search_url_includes_repo_machine_and_query() {
        var url = McpMemoryServer.BuildSearchUrl("http://x", Args("""{"query":"utc clock","limit":5}"""), "abc123", "mach-1");

        await Assert.That(url).IsEqualTo("http://x/api/memories/search?q=utc%20clock&repo=abc123&machine=mach-1&limit=5");
    }

    [Test]
    public async Task Update_body_forwards_the_expected_version() {
        var body = McpMemoryServer.BuildUpdateBody(Args("""{"id":"m1","content":"c","expected_version":"v1"}"""));

        await Assert.That(body["expected_version"]!.GetValue<string>()).IsEqualTo("v1");
        await Assert.That(body["content"]!.GetValue<string>()).IsEqualTo("c");
    }

    [Test]
    [Arguments("""{"id":"m1","expected_version":"v1"}""", """{"ok":true}""", true)]
    [Arguments("""{"id":"m1","expected_version":"v1"}""", """{"ok":true,"version":null}""", true)]
    [Arguments("""{"id":"m1","expected_version":"v1"}""", """{"ok":true,"version":"v2"}""", false)]
    [Arguments("""{"id":"m1"}""", """{"ok":true}""", false)]
    public async Task An_update_a_server_applied_without_its_version_check_says_so(string args, string body, bool noticed) {
        var result = McpMemoryServer.WithUncheckedVersionNotice(Args(args), body);

        await Assert.That(result.Contains("does not support expected_version")).IsEqualTo(noticed);
        await Assert.That(result.StartsWith(body, StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Search_url_omits_missing_context() {
        var url = McpMemoryServer.BuildSearchUrl("http://x", Args("""{"query":"a"}"""), null, null);

        await Assert.That(url).IsEqualTo("http://x/api/memories/search?q=a");
    }

    [Test]
    public async Task Save_body_defaults_to_cwd_repo_and_no_machine_tag() {
        var body = McpMemoryServer.BuildSaveBody(
            Args("""{"audience":"org","slug":"s","description":"d","content":"c","kind":"feedback"}"""),
            "abc123", "mach-1");

        await Assert.That(body["repo_hash"]!.GetValue<string>()).IsEqualTo("abc123");
        await Assert.That(body["machine_tag"]).IsNull();
        await Assert.That(body["harness"]!.GetValue<string>()).IsEqualTo("mcp");
    }

    [Test]
    public async Task Save_body_honors_global_and_machine_specific() {
        var body = McpMemoryServer.BuildSaveBody(
            Args("""{"audience":"user","slug":"s","description":"d","content":"c","kind":"preference","global":true,"machine_specific":true}"""),
            "abc123", "mach-1");

        await Assert.That(body["repo_hash"]).IsNull();
        await Assert.That(body["machine_tag"]!.GetValue<string>()).IsEqualTo("mach-1");
    }

    [Test]
    public async Task Save_body_throws_without_repo_context_unless_global() {
        var argsWithoutGlobal = Args("""{"audience":"org","slug":"s","description":"d","content":"c","kind":"feedback"}""");

        await Assert.That(() => McpMemoryServer.BuildSaveBody(argsWithoutGlobal, null, "mach-1"))
            .Throws<ArgumentException>();

        var argsWithGlobal = Args("""{"audience":"org","slug":"s","description":"d","content":"c","kind":"feedback","global":true}""");
        var body           = McpMemoryServer.BuildSaveBody(argsWithGlobal, null, "mach-1");

        await Assert.That(body["repo_hash"]).IsNull();
    }

    [Test]
    public async Task Save_body_throws_for_machine_specific_without_machine_id() {
        var args = Args("""{"audience":"user","slug":"s","description":"d","content":"c","kind":"preference","global":true,"machine_specific":true}""");

        await Assert.That(() => McpMemoryServer.BuildSaveBody(args, "abc123", null))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Save_body_always_carries_machine_context() {
        var body = McpMemoryServer.BuildSaveBody(
            Args("""{"audience":"org","slug":"s","description":"d","content":"c","kind":"feedback"}"""),
            "abc123", "mach-1");

        await Assert.That(body["machine_context"]!.GetValue<string>()).IsEqualTo("mach-1");
        await Assert.That(body["machine_tag"]).IsNull();
    }

    [Test]
    public async Task Save_body_carries_audience_project_for_project_audience() {
        var body = McpMemoryServer.BuildSaveBody(
            Args("""{"audience":"project","slug":"s","description":"d","content":"c","kind":"project","audience_project":"capacitor"}"""),
            "abc123", "mach-1");

        await Assert.That(body["audience"]!.GetValue<string>()).IsEqualTo("project");
        await Assert.That(body["audience_project"]!.GetValue<string>()).IsEqualTo("capacitor");
    }

    [Test]
    public async Task Save_body_omits_audience_project_when_absent() {
        var body = McpMemoryServer.BuildSaveBody(
            Args("""{"audience":"org","slug":"s","description":"d","content":"c","kind":"feedback"}"""),
            "abc123", "mach-1");

        await Assert.That(body["audience_project"]).IsNull();
    }

    [Test]
    public async Task Save_body_carries_project_and_keeps_repo_hash_as_fallback() {
        // A server that does not know project homes ignores the field and reads a null repo hash as
        // org-wide, so the hash rides along: there the memory lands at the repo, narrower than asked.
        var body = McpMemoryServer.BuildSaveBody(
            Args("""{"audience":"org","slug":"s","description":"d","content":"c","kind":"project","project":"capacitor"}"""),
            "abc123", "mach-1");

        await Assert.That(body["project"]!.GetValue<string>()).IsEqualTo("capacitor");
        await Assert.That(body["repo_hash"]!.GetValue<string>()).IsEqualTo("abc123");
    }

    [Test]
    public async Task Save_body_with_project_needs_no_repo_context() {
        var body = McpMemoryServer.BuildSaveBody(
            Args("""{"audience":"org","slug":"s","description":"d","content":"c","kind":"project","project":"capacitor"}"""),
            null, "mach-1");

        await Assert.That(body["project"]!.GetValue<string>()).IsEqualTo("capacitor");
        await Assert.That(body["repo_hash"]).IsNull();
    }

    [Test]
    public async Task Save_body_rejects_blank_project() {
        // A present-but-blank slug is malformed, not omitted: falling back to the repo or org home would
        // silently widen where the memory surfaces.
        var args = Args("""{"audience":"org","slug":"s","description":"d","content":"c","kind":"project","project":"   "}""");

        await Assert.That(() => McpMemoryServer.BuildSaveBody(args, "abc123", "mach-1"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Save_body_keeps_place_project_and_audience_project_apart() {
        var body = McpMemoryServer.BuildSaveBody(
            Args("""{"audience":"project","slug":"s","description":"d","content":"c","kind":"project","audience_project":"people","project":"place"}"""),
            "abc123", "mach-1");

        await Assert.That(body["audience_project"]!.GetValue<string>()).IsEqualTo("people");
        await Assert.That(body["project"]!.GetValue<string>()).IsEqualTo("place");
    }

    [Test]
    public async Task Get_url_escapes_slug_and_carries_context() {
        var url = McpMemoryServer.BuildGetUrl("http://x", Args("""{"id_or_slug":"my-slug"}"""), "abc123", "mach-1");

        await Assert.That(url).IsEqualTo("http://x/api/memories/my-slug?repo=abc123&machine=mach-1");
    }

    [Test]
    public async Task Rescope_body_carries_audience_and_team() {
        var body = McpMemoryServer.BuildRescopeBody(Args("""{"id":"m1","audience":"team","team":"payments"}"""));

        await Assert.That(body["audience"]!.GetValue<string>()).IsEqualTo("team");
        await Assert.That(body["team"]!.GetValue<string>()).IsEqualTo("payments");
        await Assert.That(body["project"]).IsNull();
    }

    [Test]
    public async Task Rescope_body_carries_project_without_audience() {
        var body = McpMemoryServer.BuildRescopeBody(Args("""{"id":"m1","project":"capacitor"}"""));

        await Assert.That(body["project"]!.GetValue<string>()).IsEqualTo("capacitor");
        await Assert.That(body["audience"]).IsNull();
    }

    [Test]
    public async Task Rescope_body_carries_audience_project_for_project_audience() {
        var body = McpMemoryServer.BuildRescopeBody(Args("""{"id":"m1","audience":"project","audience_project":"capacitor"}"""));

        await Assert.That(body["audience"]!.GetValue<string>()).IsEqualTo("project");
        await Assert.That(body["audience_project"]!.GetValue<string>()).IsEqualTo("capacitor");
        // Distinct from the place-axis project, which stays absent here.
        await Assert.That(body["project"]).IsNull();
    }

    [Test]
    public async Task Rescope_body_throws_without_audience_or_project() {
        await Assert.That(() => McpMemoryServer.BuildRescopeBody(Args("""{"id":"m1"}""")))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Rescope_body_treats_whitespace_project_as_absent() {
        // A blank slug never resolves server-side, so it counts as absent → the required-one-of check fires.
        await Assert.That(() => McpMemoryServer.BuildRescopeBody(Args("""{"id":"m1","project":"   "}""")))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Rescope_body_for_repo_target_carries_only_repo_hash() {
        var body = McpMemoryServer.BuildRescopeBody(Args("""{"id":"m1","repo_hash":"abc123"}"""));

        await Assert.That(body.Select(p => p.Key).ToArray()).IsEquivalentTo(["repo_hash"]);
        await Assert.That(body["repo_hash"]!.GetValue<string>()).IsEqualTo("abc123");
    }

    [Test]
    public async Task Rescope_body_for_global_target_carries_only_global() {
        var body = McpMemoryServer.BuildRescopeBody(Args("""{"id":"m1","global":true}"""));

        await Assert.That(body.Select(p => p.Key).ToArray()).IsEquivalentTo(["global"]);
        await Assert.That(body["global"]!.GetValue<bool>()).IsTrue();
    }

    [Test]
    public async Task Rescope_body_drops_blank_audience_fields_beside_a_place_target() {
        var body = McpMemoryServer.BuildRescopeBody(Args("""{"id":"m1","global":true,"audience":"","team":" ","audience_project":"","project":""}"""));

        await Assert.That(body.Select(p => p.Key).ToArray()).IsEquivalentTo(["global"]);
    }

    [Test]
    [Arguments("""{"id":"m1","global":true,"audience":"team","team":"eng"}""")]
    [Arguments("""{"id":"m1","global":true,"team":"eng"}""")]
    [Arguments("""{"id":"m1","repo_hash":"abc123","audience":"org"}""")]
    [Arguments("""{"id":"m1","repo_hash":"abc123","audience_project":"capacitor"}""")]
    public async Task Rescope_body_refuses_audience_fields_beside_repo_or_global(string json) {
        await Assert.That(() => McpMemoryServer.BuildRescopeBody(Args(json)))
            .Throws<ArgumentException>();
    }

    [Test]
    [Arguments("""{"id":"m1","project":"capacitor","repo_hash":"abc123"}""")]
    [Arguments("""{"id":"m1","project":"capacitor","global":true}""")]
    [Arguments("""{"id":"m1","repo_hash":"abc123","global":true}""")]
    public async Task Rescope_body_refuses_two_place_targets(string json) {
        await Assert.That(() => McpMemoryServer.BuildRescopeBody(Args(json)))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Rescope_body_refuses_blank_repo_hash() {
        await Assert.That(() => McpMemoryServer.BuildRescopeBody(Args("""{"id":"m1","repo_hash":"  "}""")))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Rescope_body_treats_global_false_as_no_target() {
        var body = McpMemoryServer.BuildRescopeBody(Args("""{"id":"m1","audience":"org","global":false}"""));

        await Assert.That(body["audience"]!.GetValue<string>()).IsEqualTo("org");
        await Assert.That(body.ContainsKey("global")).IsFalse();
    }

    [Test]
    public async Task Rescope_tool_schema_exposes_repo_hash_and_global() {
        var rescope = McpMemoryServer.BuildToolsList().Single(t => t.Name == "rescope_memory");

        await Assert.That(rescope.InputSchema.Properties.Keys).Contains("repo_hash");
        await Assert.That(rescope.InputSchema.Properties.Keys).Contains("global");
    }

    [Test]
    public async Task Tools_list_names_the_memory_and_log_entry_tools() {
        var names = McpMemoryServer.BuildToolsList().Select(t => t.Name).ToArray();

        await Assert.That(names).IsEquivalentTo([
            "search_memories", "get_memory", "save_memory", "update_memory", "rescope_memory", "archive_memory",
            "append_memory_entry", "settle_memory_entry", "reopen_memory_entry", "list_memory_entries"
        ]);
    }

    [Test]
    [Arguments("""{"audience":"org","slug":"s","description":"d","content":"c","kind":"project","form":"log"}""", "log")]
    [Arguments("""{"audience":"org","slug":"s","description":"d","content":"c","kind":"project"}""", null)]
    public async Task Save_body_carries_the_form(string args, string? form) {
        var body = McpMemoryServer.BuildSaveBody(Args(args), "abc123", "mach-1");

        await Assert.That(body["form"]?.GetValue<string>()).IsEqualTo(form);
    }

    [Test]
    public async Task Update_body_carries_the_header() {
        var body = McpMemoryServer.BuildUpdateBody(Args("""{"id":"m1","header":"h","expected_version":"v1"}"""));

        await Assert.That(body["header"]!.GetValue<string>()).IsEqualTo("h");
        await Assert.That(body["content"]).IsNull();
    }

    /// <summary>A server that predates logs ignores <c>form</c> and saves an ordinary memory, whose answer carries no
    /// <c>form</c>; a newer server always names it, including on a slug conflict that returns another memory.</summary>
    [Test]
    [Arguments("""{"form":"log"}""", """{"memory":{"memory_id":"m"},"slug_conflict":false}""", true)]
    [Arguments("""{"form":"log"}""", """{"memory":{"memory_id":"m","form":"log"},"slug_conflict":false}""", false)]
    [Arguments("""{"form":"log"}""", """{"memory":{"memory_id":"m","form":"document"},"slug_conflict":true}""", false)]
    [Arguments("""{"form":"document"}""", """{"memory":{"memory_id":"m"},"slug_conflict":false}""", false)]
    [Arguments("""{}""", """{"memory":{"memory_id":"m"},"slug_conflict":false}""", false)]
    public async Task A_log_save_a_server_made_as_an_ordinary_memory_says_so(string args, string body, bool noticed) {
        var result = McpMemoryServer.WithLogFormNotice(Args(args), body);

        await Assert.That(result.Contains("does not support log memories")).IsEqualTo(noticed);
        await Assert.That(result.StartsWith(body, StringComparison.Ordinal)).IsTrue();
    }

    [Test]
    public async Task Save_tool_schema_exposes_place_project_beside_audience_project() {
        var save = McpMemoryServer.BuildToolsList().Single(t => t.Name == "save_memory");

        await Assert.That(save.InputSchema.Properties.Keys).Contains("project");
        await Assert.That(save.InputSchema.Properties.Keys).Contains("audience_project");
        await Assert.That(save.InputSchema.Required).DoesNotContain("project");
    }
}
