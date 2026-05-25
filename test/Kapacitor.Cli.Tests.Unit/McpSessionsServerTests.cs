using System.Text.Json;
using System.Text.Json.Nodes;
using Kapacitor.Cli.Commands;

namespace Kapacitor.Cli.Tests.Unit;

public class McpSessionsServerTests {
    [Test]
    public async Task BuildSearchUrl_no_args_no_cwd_hash() {
        var url = McpSessionsServer.BuildSearchUrl("http://srv", args: null, cwdRepoHash: null);

        await Assert.That(url).IsEqualTo("http://srv/api/sessions/search");
    }

    [Test]
    public async Task BuildSearchUrl_with_query_only() {
        var url = McpSessionsServer.BuildSearchUrl(
            "http://srv",
            new JsonObject { ["query"] = "batch" },
            cwdRepoHash: null
        );

        await Assert.That(url).IsEqualTo("http://srv/api/sessions/search?q=batch");
    }

    [Test]
    public async Task BuildSearchUrl_with_query_author_limit() {
        var url = McpSessionsServer.BuildSearchUrl(
            "http://srv",
            new JsonObject {
                ["query"]  = "retry logic",
                ["author"] = "alice",
                ["limit"]  = 25
            },
            cwdRepoHash: null
        );

        await Assert.That(url).Contains("q=retry%20logic");
        await Assert.That(url).Contains("author=alice");
        await Assert.That(url).Contains("limit=25");
    }

    [Test]
    public async Task BuildSearchUrl_repo_all_omits_repo_param() {
        var url = McpSessionsServer.BuildSearchUrl(
            "http://srv",
            new JsonObject { ["repo"] = "all" },
            cwdRepoHash: "abc1234567890def"
        );

        await Assert.That(url).IsEqualTo("http://srv/api/sessions/search");
    }

    [Test]
    public async Task BuildSearchUrl_no_repo_uses_cwd_hash() {
        var url = McpSessionsServer.BuildSearchUrl(
            "http://srv",
            args: null,
            cwdRepoHash: "abc1234567890def"
        );

        await Assert.That(url).IsEqualTo("http://srv/api/sessions/search?repo=abc1234567890def");
    }

    [Test]
    public async Task BuildSearchUrl_explicit_repo_overrides_cwd_hash() {
        var url = McpSessionsServer.BuildSearchUrl(
            "http://srv",
            new JsonObject { ["repo"] = "kurrent-io/kapacitor" },
            cwdRepoHash: "abc1234567890def"
        );

        await Assert.That(url).IsEqualTo("http://srv/api/sessions/search?repo=kurrent-io%2Fkapacitor");
    }

    [Test]
    public async Task BuildSearchUrl_author_github_id_takes_precedence_via_query_param() {
        var url = McpSessionsServer.BuildSearchUrl(
            "http://srv",
            new JsonObject { ["author_github_id"] = 12345 },
            cwdRepoHash: null
        );

        await Assert.That(url).IsEqualTo("http://srv/api/sessions/search?author_github_id=12345");
    }

    [Test]
    public async Task ProjectRecapToSummary_empty_array() {
        var projected = McpSessionsServer.ProjectRecapToSummary("[]");

        using var doc = JsonDocument.Parse(projected);
        await Assert.That(doc.RootElement.GetProperty("summary_text").GetString()).IsEqualTo("");
        await Assert.That(doc.RootElement.GetProperty("plan").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task ProjectRecapToSummary_one_whats_done_one_plan() {
        const string body = """
            [
              {"type": "whats_done", "content": "Implemented feature X"},
              {"type": "plan",       "content": "Step 1: do thing"}
            ]
            """;

        var projected = McpSessionsServer.ProjectRecapToSummary(body);

        using var doc = JsonDocument.Parse(projected);
        await Assert.That(doc.RootElement.GetProperty("summary_text").GetString()).IsEqualTo("Implemented feature X");
        await Assert.That(doc.RootElement.GetProperty("plan").GetString()).IsEqualTo("Step 1: do thing");
    }

    [Test]
    public async Task ProjectRecapToSummary_multiple_whats_done_latest_wins() {
        const string body = """
            [
              {"type": "whats_done", "content": "First pass"},
              {"type": "whats_done", "content": "Second pass"},
              {"type": "whats_done", "content": "Final"}
            ]
            """;

        var projected = McpSessionsServer.ProjectRecapToSummary(body);

        using var doc = JsonDocument.Parse(projected);
        await Assert.That(doc.RootElement.GetProperty("summary_text").GetString()).IsEqualTo("Final");
        await Assert.That(doc.RootElement.GetProperty("plan").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task ProjectRecapToSummary_escapes_special_chars() {
        const string body = """
            [
              {"type": "whats_done", "content": "Has \"quotes\" and \nnewlines"}
            ]
            """;

        var projected = McpSessionsServer.ProjectRecapToSummary(body);

        // Must be parseable as JSON
        using var doc = JsonDocument.Parse(projected);
        await Assert.That(doc.RootElement.GetProperty("summary_text").GetString()).IsEqualTo("Has \"quotes\" and \nnewlines");
    }
}
