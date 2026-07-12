using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

public class McpWorkItemsServerTests {
    const string CapacitorSessionIdEnvVar = "KCAP_SESSION_ID";
    const string CodexThreadIdEnvVar      = "CODEX_THREAD_ID";

    // Shares ArgParsingTests' NotInParallel key: both suites mutate the same process-global
    // KCAP_SESSION_ID / CODEX_THREAD_ID env vars, so tests in either must not interleave.
    const string SessionEnvVarMutation = "SessionEnvVarMutation";

    static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    [Test]
    public async Task Resolve_session_id_prefers_explicit_argument() {
        var id = McpWorkItemsServer.ResolveSessionId(Args("""{"session_id":"explicit1"}"""));

        await Assert.That(id).IsEqualTo("explicit1");
    }

    [Test]
    public async Task Resolve_session_id_strips_dashes_from_explicit_argument() {
        // Matches ArgParsing.ResolveSessionIdFromEnv's normalization so an explicit dashed GUID
        // (e.g. copy-pasted from a UI) resolves to the same dashless key as the ambient env var.
        var id = McpWorkItemsServer.ResolveSessionId(Args("""{"session_id":"1234abcd-56ef-78ab-90cd-1234567890ab"}"""));

        await Assert.That(id).IsEqualTo("1234abcd56ef78ab90cd1234567890ab");
    }

    [Test]
    [NotInParallel(SessionEnvVarMutation)]
    public async Task Resolve_session_id_falls_back_to_env_when_argument_missing() {
        var savedKap = Environment.GetEnvironmentVariable(CapacitorSessionIdEnvVar);
        var savedCdx = Environment.GetEnvironmentVariable(CodexThreadIdEnvVar);
        Environment.SetEnvironmentVariable(CapacitorSessionIdEnvVar, "envsess1");
        Environment.SetEnvironmentVariable(CodexThreadIdEnvVar, null);

        try {
            var id = McpWorkItemsServer.ResolveSessionId(new JsonObject());

            await Assert.That(id).IsEqualTo("envsess1");
        } finally {
            Environment.SetEnvironmentVariable(CapacitorSessionIdEnvVar, savedKap);
            Environment.SetEnvironmentVariable(CodexThreadIdEnvVar, savedCdx);
        }
    }

    [Test]
    [NotInParallel(SessionEnvVarMutation)]
    public async Task Resolve_session_id_throws_when_neither_argument_nor_env_present() {
        var savedKap = Environment.GetEnvironmentVariable(CapacitorSessionIdEnvVar);
        var savedCdx = Environment.GetEnvironmentVariable(CodexThreadIdEnvVar);
        Environment.SetEnvironmentVariable(CapacitorSessionIdEnvVar, null);
        Environment.SetEnvironmentVariable(CodexThreadIdEnvVar, null);

        try {
            var ex = Assert.Throws<ArgumentException>(() => McpWorkItemsServer.ResolveSessionId(new JsonObject()));

            await Assert.That(ex!.Message).IsEqualTo(McpWorkItemsServer.NoSessionIdMessage);
        } finally {
            Environment.SetEnvironmentVariable(CapacitorSessionIdEnvVar, savedKap);
            Environment.SetEnvironmentVariable(CodexThreadIdEnvVar, savedCdx);
        }
    }

    [Test]
    public async Task Declare_body_carries_session_id_and_issue_key() {
        var body = McpWorkItemsServer.BuildDeclareBody(Args("""{"session_id":"s1","issue_key":"AI-1234"}"""));

        await Assert.That(body["session_id"]!.GetValue<string>()).IsEqualTo("s1");
        await Assert.That(body["issue_key"]!.GetValue<string>()).IsEqualTo("AI-1234");
        await Assert.That(body["pr_number"]).IsNull();
        await Assert.That(body["work_item_id"]).IsNull();
        await Assert.That(body["new_title"]).IsNull();
    }

    [Test]
    public async Task Declare_body_carries_pr_number() {
        var body = McpWorkItemsServer.BuildDeclareBody(Args("""{"session_id":"s1","pr_number":123}"""));

        await Assert.That(body["pr_number"]!.GetValue<int>()).IsEqualTo(123);
    }

    [Test]
    public async Task Declare_body_carries_work_item_id() {
        var body = McpWorkItemsServer.BuildDeclareBody(Args("""{"session_id":"s1","work_item_id":"wi-9"}"""));

        await Assert.That(body["work_item_id"]!.GetValue<string>()).IsEqualTo("wi-9");
    }

    [Test]
    public async Task Declare_body_carries_new_title() {
        var body = McpWorkItemsServer.BuildDeclareBody(Args("""{"session_id":"s1","new_title":"Investigate flaky test"}"""));

        await Assert.That(body["new_title"]!.GetValue<string>()).IsEqualTo("Investigate flaky test");
    }

    [Test]
    public async Task Session_url_escapes_and_resolves_explicit_session_id() {
        var url = McpWorkItemsServer.BuildSessionUrl("http://x", Args("""{"session_id":"sess a/b"}"""));

        await Assert.That(url).IsEqualTo("http://x/api/work-items/session/sess%20a%2Fb");
    }

    // ── flow-review round 2 findings ─────────────────────────────────────────

    [Test]
    public async Task Decode_method_returns_null_for_wrong_shaped_method_instead_of_throwing() {
        // {"id":1,"method":{}} must yield an invalid-request response, never kill the stdio loop.
        var method = McpWorkItemsServer.DecodeMethod(Args("""{"id":1,"method":{}}"""));

        await Assert.That(method).IsNull();
    }

    [Test]
    public async Task Declare_body_rejects_string_pr_number_instead_of_dropping_it() {
        // A malformed two-selector declare (issue_key + string pr_number) must FAIL — silently
        // dropping the wrong-shaped selector would let the server's exactly-one rule pass and
        // perform an attach the caller never validly requested.
        var ex = Assert.Throws<ArgumentException>(
            () => McpWorkItemsServer.BuildDeclareBody(Args("""{"session_id":"s1","issue_key":"AI-1","pr_number":"123"}""")));

        await Assert.That(ex!.Message).Contains("pr_number");
    }

    [Test]
    public async Task Declare_body_rejects_object_shaped_pr_number() {
        var ex = Assert.Throws<ArgumentException>(
            () => McpWorkItemsServer.BuildDeclareBody(Args("""{"session_id":"s1","pr_number":{}}""")));

        await Assert.That(ex!.Message).Contains("pr_number");
    }

    [Test]
    public async Task Declare_body_rejects_fractional_pr_number_via_raw_token() {
        // Raw-token validation (JsonElement.TryGetInt32) — a fractional part below double
        // precision must still reject; the lossy double round-trip would have accepted it.
        var ex = Assert.Throws<ArgumentException>(
            () => McpWorkItemsServer.BuildDeclareBody(Args("""{"session_id":"s1","pr_number":2147483646.0000000001}""")));

        await Assert.That(ex!.Message).Contains("pr_number");
    }

    [Test]
    public async Task Declare_body_rejects_out_of_range_pr_number() {
        var ex = Assert.Throws<ArgumentException>(
            () => McpWorkItemsServer.BuildDeclareBody(Args("""{"session_id":"s1","pr_number":2147483648}""")));

        await Assert.That(ex!.Message).Contains("pr_number");
    }

    [Test]
    public async Task Tools_list_has_two_tools() {
        var tools = McpWorkItemsServer.BuildToolsList();

        await Assert.That(tools.Length).IsEqualTo(2);
        await Assert.That(tools.Select(t => t.Name).ToArray()).IsEquivalentTo(new[] { "declare_work_item", "get_session_work_items" });
    }
}
