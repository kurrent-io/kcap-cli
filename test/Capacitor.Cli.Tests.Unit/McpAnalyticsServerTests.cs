using System.Net;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

public class McpAnalyticsServerTests {
    static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    [Test]
    public async Task Query_body_defaults_to_cwd_repo_scope() {
        var body = McpAnalyticsServer.BuildQueryBody(Args("""{"sql":"SELECT vendor FROM v_an_sessions"}"""), "abc123");

        await Assert.That(body["sql"]!.GetValue<string>()).IsEqualTo("SELECT vendor FROM v_an_sessions");
        await Assert.That(body["repos"]!.AsArray().Count).IsEqualTo(1);
        await Assert.That(body["repos"]![0]!.GetValue<string>()).IsEqualTo("abc123");
        await Assert.That(body.ContainsKey("max_rows")).IsFalse();
    }

    [Test]
    public async Task Query_body_global_scope_sends_null_repos() {
        var body = McpAnalyticsServer.BuildQueryBody(Args("""{"sql":"SELECT 1 AS n FROM v_an_sessions","scope":"global"}"""), "abc123");

        await Assert.That(body.ContainsKey("repos")).IsTrue();
        await Assert.That(body["repos"]).IsNull();
    }

    [Test]
    public async Task Query_body_fails_closed_when_repo_scope_is_unresolvable() {
        // No cwd repo hash + default (repo) scope must never silently widen to global.
        await Assert.That(() => McpAnalyticsServer.BuildQueryBody(Args("""{"sql":"SELECT 1 AS n FROM v_an_sessions"}"""), null))
            .Throws<ArgumentException>();

        // Explicit global works without a cwd repo.
        var body = McpAnalyticsServer.BuildQueryBody(Args("""{"sql":"SELECT 1 AS n FROM v_an_sessions","scope":"global"}"""), null);
        await Assert.That(body["repos"]).IsNull();
    }

    [Test]
    public async Task Query_body_rejects_unknown_scope_and_missing_sql() {
        await Assert.That(() => McpAnalyticsServer.BuildQueryBody(Args("""{"sql":"SELECT 1","scope":"everything"}"""), "abc123"))
            .Throws<ArgumentException>();
        await Assert.That(() => McpAnalyticsServer.BuildQueryBody(Args("""{"scope":"global"}"""), "abc123"))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Query_body_passes_max_rows_through() {
        var body = McpAnalyticsServer.BuildQueryBody(Args("""{"sql":"SELECT vendor FROM v_an_sessions","max_rows":50}"""), "abc123");

        await Assert.That(body["max_rows"]!.GetValue<int>()).IsEqualTo(50);
    }

    [Test]
    public async Task Map_response_unwraps_schema_text_envelope() {
        var text = McpAnalyticsServer.MapResponse("get_analytics_schema", HttpStatusCode.OK,
            """{"text":"Views and columns:\n  v_an_sessions(...)","max_rows":300}""", out var isError);

        await Assert.That(isError).IsFalse();
        await Assert.That(text).StartsWith("Views and columns:");
        await Assert.That(text).DoesNotContain("max_rows");
    }

    [Test]
    public async Task Map_response_appends_truncation_trailer_only_when_truncated() {
        var truncated = McpAnalyticsServer.MapResponse("query_analytics", HttpStatusCode.OK,
            """{"columns":["n"],"rows":[{"n":1}],"row_count":1,"truncated":true,"max_rows":300}""", out var isError1);
        await Assert.That(isError1).IsFalse();
        await Assert.That(truncated).Contains("WARNING: result truncated to 300 rows");
        await Assert.That(truncated).Contains("Aggregate in SQL");

        var full = McpAnalyticsServer.MapResponse("query_analytics", HttpStatusCode.OK,
            """{"columns":["n"],"rows":[{"n":1}],"row_count":1,"truncated":false,"max_rows":300}""", out var isError2);
        await Assert.That(isError2).IsFalse();
        await Assert.That(full).DoesNotContain("truncated to");
    }

    [Test]
    public async Task Map_response_surfaces_rejection_reason_for_self_repair() {
        var text = McpAnalyticsServer.MapResponse("query_analytics", HttpStatusCode.BadRequest,
            """{"title":"query_rejected","status":400,"detail":"disallowed table: sessions"}""", out var isError);

        await Assert.That(isError).IsTrue();
        await Assert.That(text).IsEqualTo("REJECTED: disallowed table: sessions");
    }

    [Test]
    public async Task Map_response_maps_statuses_to_actionable_messages() {
        await Assert.That(McpAnalyticsServer.MapResponse("query_analytics", HttpStatusCode.Unauthorized, "", out _))
            .IsEqualTo(McpAnalyticsServer.NotLoggedInMessage);
        await Assert.That(McpAnalyticsServer.MapResponse("query_analytics", HttpStatusCode.NotFound, "", out _))
            .IsEqualTo(McpAnalyticsServer.NotSupportedMessage);
        await Assert.That(McpAnalyticsServer.MapResponse("query_analytics", HttpStatusCode.RequestTimeout, "", out _))
            .Contains("timed out");
        await Assert.That(McpAnalyticsServer.MapResponse("query_analytics", HttpStatusCode.InternalServerError, "boom", out _))
            .Contains("HTTP 500");
    }

    [Test]
    public async Task Tools_list_exposes_the_two_read_only_tools() {
        var tools = McpAnalyticsServer.BuildToolsList();

        await Assert.That(tools.Select(t => t.Name).ToArray())
            .IsEquivalentTo(new[] { "get_analytics_schema", "query_analytics" });

        var query = tools.Single(t => t.Name == "query_analytics");
        await Assert.That(query.InputSchema.Required).IsEquivalentTo(new[] { "sql" });
        await Assert.That(query.InputSchema.Properties.Keys.ToArray())
            .IsEquivalentTo(new[] { "sql", "scope", "max_rows" });
    }
}
