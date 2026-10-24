using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.PrDetection;
using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>The log-memory tools of <c>kcap mcp memory</c>, dispatched against a recording server: the route and body
/// each one sends, and what an agent is told when the server predates logs.</summary>
public class McpMemoryServerLogTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string MemoryId = "0123456789abcdef0123456789abcdef";
    const string EntryId  = "fedcba9876543210fedcba9876543210";

    McpMemoryServer Server() =>
        new(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), new FixedCapacitorHttpClient(), NoTelemetry.Startup,
            new GitProviderRouter(), new WorkingDirectory(AppContext.BaseDirectory), TimeProvider.System);

    sealed record Call(string Text, bool IsError, ILogEntry? Request);

    async Task<Call> CallAsync(string tool, string arguments, int status = 200, string body = "{}") {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().UsingAnyMethod())
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body));

        using var client = new HttpClient();
        var request = new JsonObject {
            ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = JsonNode.Parse(arguments) }
        };

        var response = await Server().HandleToolCallAsync(JsonNode.Parse("1")!, request, client, server.Url!, "repo-hash", "mach-1");
        var result   = JsonNode.Parse(response)!["result"]!;

        return new(result["content"]![0]!["text"]!.GetValue<string>(), result["isError"]?.GetValue<bool>() ?? false, server.LogEntries.SingleOrDefault());
    }

    static JsonNode SentBody(Call call) => JsonNode.Parse(call.Request!.RequestMessage.Body!)!;

    static string SentQuery(Call call) => (call.Request!.RequestMessage.RawQuery ?? "").TrimStart('?');

    [Test]
    public async Task Append_posts_the_entry_to_the_log_resolved_like_get_memory() {
        var call = await CallAsync("append_memory_entry",
            """{"id_or_slug":"cross-account-inbox","title":"Rotate the key","body":"Before Friday.","idempotency_key":"k-1"}""",
            body: """{"entry":{"entry_id":"e"},"already_appended":false,"open_entries":3}""");

        await Assert.That(call.IsError).IsFalse();
        await Assert.That(call.Text).IsEqualTo("""{"entry":{"entry_id":"e"},"already_appended":false,"open_entries":3}""");
        await Assert.That(call.Request!.RequestMessage.Method).IsEqualTo("POST");
        await Assert.That(call.Request.RequestMessage.Path).IsEqualTo("/api/memories/cross-account-inbox/entries");
        await Assert.That(SentQuery(call)).IsEqualTo("repo=repo-hash&machine=mach-1");
        await Assert.That(JsonNode.DeepEquals(SentBody(call), JsonNode.Parse(
            """{"title":"Rotate the key","body":"Before Friday.","idempotency_key":"k-1","source_session_id":null,"harness":"mcp"}"""))).IsTrue();
    }

    [Test]
    [Arguments("""{"title":"t","body":"b"}""")]
    [Arguments("""{"id_or_slug":"inbox","body":"b"}""")]
    [Arguments("""{"id_or_slug":"inbox","title":"t"}""")]
    [Arguments("""{"id_or_slug":"inbox","title":"","body":"b"}""")]
    public async Task Append_without_a_log_title_or_body_sends_nothing(string arguments) {
        var call = await CallAsync("append_memory_entry", arguments);

        await Assert.That(call.IsError).IsTrue();
        await Assert.That(call.Request).IsNull();
    }

    [Test]
    [Arguments("append_memory_entry", """{"id_or_slug":"inbox","title":42,"body":"b"}""", "title")]
    [Arguments("append_memory_entry", """{"id_or_slug":"inbox","title":"t","body":{"text":"b"}}""", "body")]
    [Arguments("append_memory_entry", """{"id_or_slug":"inbox","title":"t","body":"b","idempotency_key":7}""", "idempotency_key")]
    [Arguments("settle_memory_entry", """{"id":"ID","entry_id":42}""", "entry_id")]
    [Arguments("settle_memory_entry", """{"id":"ID","entry_id":"E","resolution":true}""", "resolution")]
    [Arguments("reopen_memory_entry", """{"id":"ID","entry_id":["E"]}""", "entry_id")]
    [Arguments("list_memory_entries", """{"id":"ID","status":1}""", "status")]
    public async Task A_wrong_typed_argument_is_refused_by_name_and_sends_nothing(string tool, string arguments, string field) {
        var call = await CallAsync(tool, arguments.Replace("\"ID\"", $"\"{MemoryId}\"").Replace("\"E\"", $"\"{EntryId}\""));

        await Assert.That(call.IsError).IsTrue();
        await Assert.That(call.Text).IsEqualTo($"Error: '{field}' must be a string.");
        await Assert.That(call.Request).IsNull();
    }

    [Test]
    [Arguments("search_memories", """{"query":5}""", "query", "string")]
    [Arguments("save_memory", """{"audience":"org","slug":5,"description":"d","content":"c","kind":"project"}""", "slug", "string")]
    [Arguments("save_memory", """{"audience":"org","slug":"s","description":"d","content":"c","kind":"project","team":["t"]}""", "team", "string")]
    [Arguments("save_memory", """{"audience":"org","slug":"s","description":"d","content":"c","kind":"project","global":"yes"}""", "global", "boolean")]
    [Arguments("update_memory", """{"id":"m1","kind":3}""", "kind", "string")]
    [Arguments("rescope_memory", """{"id":"m1","audience":3}""", "audience", "string")]
    [Arguments("rescope_memory", """{"id":"m1","global":true,"team":1}""", "team", "string")]
    public async Task A_wrong_typed_argument_to_a_memory_tool_is_refused_by_name_and_sends_nothing(
            string tool, string arguments, string field, string type) {
        var call = await CallAsync(tool, arguments);

        await Assert.That(call.IsError).IsTrue();
        await Assert.That(call.Text).IsEqualTo($"Error: '{field}' must be a {type}.");
        await Assert.That(call.Request).IsNull();
    }

    [Test]
    [Arguments("\"5\"")]
    [Arguments("2.5")]
    [Arguments("{\"n\":5}")]
    public async Task A_list_limit_that_is_not_an_integer_is_refused_and_sends_nothing(string limit) {
        var call = await CallAsync("list_memory_entries", $$"""{"id":"{{MemoryId}}","limit":{{limit}}}""");

        await Assert.That(call.IsError).IsTrue();
        await Assert.That(call.Text).IsEqualTo("Error: 'limit' must be an integer.");
        await Assert.That(call.Request).IsNull();
    }

    [Test]
    public async Task A_search_limit_that_is_not_an_integer_is_refused_and_sends_nothing() {
        var call = await CallAsync("search_memories", """{"query":"q","limit":"5"}""");

        await Assert.That(call.IsError).IsTrue();
        await Assert.That(call.Text).IsEqualTo("Error: 'limit' must be an integer.");
        await Assert.That(call.Request).IsNull();
    }

    [Test]
    public async Task Settle_posts_the_resolution_to_the_entry_of_that_log() {
        var call = await CallAsync("settle_memory_entry", $$"""{"id":"{{MemoryId}}","entry_id":"{{EntryId}}","resolution":"Rotated."}""");

        await Assert.That(call.Request!.RequestMessage.Method).IsEqualTo("POST");
        await Assert.That(call.Request.RequestMessage.Path).IsEqualTo($"/api/memories/{MemoryId}/entries/{EntryId}/settle");
        await Assert.That(JsonNode.DeepEquals(SentBody(call), JsonNode.Parse("""{"resolution":"Rotated."}"""))).IsTrue();
    }

    [Test]
    public async Task Reopen_posts_to_the_entry_of_that_log() {
        var call = await CallAsync("reopen_memory_entry", $$"""{"id":"{{MemoryId}}","entry_id":"{{EntryId}}"}""");

        await Assert.That(call.IsError).IsFalse();
        await Assert.That(call.Request!.RequestMessage.Method).IsEqualTo("POST");
        await Assert.That(call.Request.RequestMessage.Path).IsEqualTo($"/api/memories/{MemoryId}/entries/{EntryId}/reopen");
    }

    [Test]
    [Arguments("settle_memory_entry")]
    [Arguments("reopen_memory_entry")]
    public async Task A_transition_without_an_entry_id_sends_nothing(string tool) {
        var call = await CallAsync(tool, $$"""{"id":"{{MemoryId}}"}""");

        await Assert.That(call.IsError).IsTrue();
        await Assert.That(call.Request).IsNull();
    }

    [Test]
    [Arguments("""{"id":"ID"}""", "")]
    [Arguments("""{"id":"ID","status":"settled","before":"c/1=","limit":5}""", "status=settled&before=c%2F1%3D&limit=5")]
    public async Task List_pages_one_status_of_the_log(string arguments, string query) {
        var call = await CallAsync("list_memory_entries", arguments.Replace("ID", MemoryId));

        await Assert.That(call.Request!.RequestMessage.Method).IsEqualTo("GET");
        await Assert.That(call.Request.RequestMessage.Path).IsEqualTo($"/api/memories/{MemoryId}/entries");
        await Assert.That(SentQuery(call)).IsEqualTo(query);
    }

    /// <summary>A server without the entry routes answers 404 with an empty body or a bare status problem; every 404
    /// the entry routes themselves answer names what was not found in <c>detail</c>, which must reach the agent as is.</summary>
    [Test]
    [Arguments("append_memory_entry", "", true)]
    [Arguments("append_memory_entry", """{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.5","title":"Not Found","status":404}""", true)]
    [Arguments("append_memory_entry", """{"title":"Not Found","status":404,"detail":"Memory 'inbox' not found."}""", false)]
    [Arguments("settle_memory_entry", "", true)]
    [Arguments("reopen_memory_entry", "", true)]
    [Arguments("list_memory_entries", "", true)]
    [Arguments("list_memory_entries", """{"title":"Not Found","status":404,"detail":"Memory 'x' not found."}""", false)]
    public async Task A_server_without_the_entry_routes_is_named_as_too_old_for_logs(string tool, string body, bool tooOld) {
        var arguments = $$"""{"id_or_slug":"inbox","id":"{{MemoryId}}","entry_id":"{{EntryId}}","title":"t","body":"b"}""";

        var call = await CallAsync(tool, arguments, status: 404, body: body);

        await Assert.That(call.IsError).IsTrue();
        await Assert.That(call.Text.Contains("does not support log memories")).IsEqualTo(tooOld);
        if (!tooOld) await Assert.That(call.Text).Contains(body);
    }

    /// <summary>Without the entry routes, a POST to one reaches the GET-only page fallback, which answers 405.</summary>
    [Test]
    [Arguments("append_memory_entry")]
    [Arguments("settle_memory_entry")]
    [Arguments("reopen_memory_entry")]
    [Arguments("list_memory_entries")]
    public async Task A_405_on_an_entry_route_is_named_as_too_old_for_logs(string tool) {
        var arguments = $$"""{"id_or_slug":"inbox","id":"{{MemoryId}}","entry_id":"{{EntryId}}","title":"t","body":"b"}""";

        var call = await CallAsync(tool, arguments, status: 405,
            body: """{"type":"https://tools.ietf.org/html/rfc9110#section-15.5.6","title":"Method Not Allowed","status":405}""");

        await Assert.That(call.IsError).IsTrue();
        await Assert.That(call.Text).Contains("does not support log memories");
    }

    [Test]
    public async Task A_missing_document_memory_is_not_mistaken_for_an_older_server() {
        var call = await CallAsync("get_memory", """{"id_or_slug":"gone"}""", status: 404, body: "");

        await Assert.That(call.Text).DoesNotContain("does not support log memories");
    }
}
