using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class McpJudgeServerTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    [Test]
    public async Task get_session_recap_forwards_session_id_and_chain_flag() {
        _server.Given(Request.Create()
                .WithPath("/api/sessions/abc-123/recap")
                .WithParam("chain", "true")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBody("""[{"summary":"did a thing"}]"""));

        using var http = new HttpClient();

        var result = await McpJudgeServer.HandleToolCallForTests(
            toolName: "get_session_recap",
            arguments: new JsonObject { ["session_id"] = "abc-123" },
            client: http,
            baseUrl: _server.Url!,
            expectedSessionId: "abc-123"
        );

        using var doc = JsonDocument.Parse(result);

        await Assert.That(
                doc.RootElement.GetProperty("result")
                    .GetProperty("content")[0]
                    .GetProperty("text")
                    .GetString()
            )
            .IsEqualTo("""[{"summary":"did a thing"}]""");
    }

    [Test]
    public async Task get_session_recap_rejects_mismatched_session_id() {
        using var http = new HttpClient();

        var result = await McpJudgeServer.HandleToolCallForTests(
            toolName: "get_session_recap",
            arguments: new JsonObject { ["session_id"] = "other-session" },
            client: http,
            baseUrl: _server.Url!,
            expectedSessionId: "abc-123"
        );

        using var doc = JsonDocument.Parse(result);
        var content = doc.RootElement.GetProperty("result");

        await Assert.That(content.GetProperty("isError").GetBoolean()).IsTrue();
        await Assert.That(content.GetProperty("content")[0].GetProperty("text").GetString())
            .Contains("does not match this judge's bound session");
    }

    [Test]
    public async Task get_session_errors_forwards_the_chain_flag_and_pages_the_digest() {
        _server.Given(Request.Create()
                .WithPath("/api/sessions/abc-123/errors")
                .WithParam("chain", "true")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBody("""
                    [{"session_id":"abc-123","event_number":4,"tool_name":"Bash","tool_input":{"command":"make"},"error":"exit 1"},
                     {"session_id":"abc-123","event_number":9,"tool_name":"Bash","tool_input":{"command":"make"},"error":"exit 2"}]
                    """));

        using var doc = JsonDocument.Parse(await CallText("get_session_errors",
            new JsonObject { ["session_id"] = "abc-123", ["offset"] = 1, ["limit"] = 1 }));
        var root = doc.RootElement;

        await Assert.That(root.GetProperty("total_failures").GetInt32()).IsEqualTo(2);
        await Assert.That(root.GetProperty("failures").GetArrayLength()).IsEqualTo(1);
        await Assert.That(root.GetProperty("failures")[0].GetProperty("ref").GetString()).IsEqualTo("#9");
        await Assert.That(root.TryGetProperty("next_offset", out _)).IsFalse();
    }

    [Test]
    [Arguments("get_session_errors", "limit", 0)]
    [Arguments("get_session_errors", "limit", 101)]
    [Arguments("get_session_errors", "offset", -1)]
    [Arguments("get_tool_result", "offset", -1)]
    [Arguments("get_session_summary", "offset", -1)]
    public async Task an_out_of_range_page_argument_is_a_tool_error(string tool, string argument, int value) {
        _server.Given(Request.Create().UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithBody("[]"));

        using var http = new HttpClient();
        var result = await McpJudgeServer.HandleToolCallForTests(
            tool, new JsonObject { ["session_id"] = "abc-123", ["call_id"] = "toolu_1", [argument] = value }, http, _server.Url!, "abc-123");

        using var doc = JsonDocument.Parse(result);
        var content = doc.RootElement.GetProperty("result");
        await Assert.That(content.GetProperty("isError").GetBoolean()).IsTrue();
        await Assert.That(content.GetProperty("content")[0].GetProperty("text").GetString()).StartsWith($"Error: {argument} must be");
    }

    [Test]
    public async Task an_oversized_result_is_cut_with_a_hint_instead_of_spilling() {
        var events = new string('x', JudgeResultCap.MaxChars * 2);
        _server.Given(Request.Create().WithPath("/api/review/sessions/abc-123/transcript").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(events));

        var text = await CallText("get_transcript", new JsonObject { ["session_id"] = "abc-123" });

        await Assert.That(text.Length).IsLessThan(JudgeResultCap.MaxChars + 200);
        await Assert.That(text).EndsWith("Pass a smaller take, or skip past what you have read.]");
    }

    [Test]
    public async Task get_tool_result_pages_the_output_inside_its_envelope() {
        var output = new string('a', JudgeResultCap.MaxChars) + "TAIL";
        _server.Given(Request.Create().WithPath("/api/sessions/abc-123/tool-results/toolu_xyz").WithParam("agent_id", "ag1").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                $$"""{"call_id":"toolu_xyz","tool":"Bash","is_error":true,"output_bytes":{{output.Length}},"output":"{{output}}"}"""));

        static JsonObject Args(int? offset) => new() { ["session_id"] = "abc-123", ["call_id"] = "toolu_xyz", ["agent_id"] = "ag1", ["offset"] = offset };

        using var first  = JsonDocument.Parse(await CallText("get_tool_result", Args(null)));
        using var second = JsonDocument.Parse(await CallText("get_tool_result", Args(first.RootElement.GetProperty("next_offset").GetInt32())));

        await Assert.That(first.RootElement.GetProperty("tool").GetString()).IsEqualTo("Bash");
        await Assert.That(first.RootElement.GetProperty("output").GetString() + second.RootElement.GetProperty("output").GetString()).IsEqualTo(output);
        await Assert.That(second.RootElement.TryGetProperty("next_offset", out _)).IsFalse();
    }

    [Test]
    public async Task get_tool_result_keeps_an_escaped_page_within_the_cap() {
        var output = string.Concat(Enumerable.Repeat("\\n", 30_000));
        _server.Given(Request.Create().WithPath("/api/sessions/abc-123/tool-results/toolu_nl").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                $$"""{"call_id":"toolu_nl","tool":"Bash","is_error":false,"output_bytes":30000,"output":"{{output}}"}"""));

        var text = await CallText("get_tool_result", new JsonObject { ["session_id"] = "abc-123", ["call_id"] = "toolu_nl" });
        using var page = JsonDocument.Parse(text);

        await Assert.That(text.Length).IsLessThanOrEqualTo(JudgeResultCap.MaxChars);
        await Assert.That(page.RootElement.GetProperty("next_offset").GetInt32())
            .IsEqualTo(page.RootElement.GetProperty("output").GetString()!.Length);
    }

    [Test]
    public async Task search_session_cuts_a_whole_event_snippet_to_a_window_around_the_term() {
        var snippet = new string('a', 2_000) + " NEEDLE " + new string('b', 2_000);
        _server.Given(Request.Create().WithPath("/api/sessions/abc-123/search").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(
                $$"""{"session_id":"abc-123","query":"needle","total_candidates":1,"results":[{"event_index":3,"snippet":"{{snippet}}","score":0.5}]}"""));

        using var doc = JsonDocument.Parse(await CallText("search_session", new JsonObject { ["session_id"] = "abc-123", ["query"] = "needle" }));
        var hit = doc.RootElement.GetProperty("results")[0];

        await Assert.That(hit.GetProperty("snippet").GetString()!).Contains("NEEDLE");
        await Assert.That(hit.GetProperty("snippet").GetString()!.Length).IsLessThan(600);
        await Assert.That(hit.GetProperty("event_index").GetInt32()).IsEqualTo(3);
    }

    async Task<string> CallText(string tool, JsonObject arguments) {
        using var http = new HttpClient();
        var result = await McpJudgeServer.HandleToolCallForTests(tool, arguments, http, _server.Url!, "abc-123");
        using var doc = JsonDocument.Parse(result);
        return doc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
    }

    [Test]
    public async Task get_transcript_forwards_session_id_only() {
        _server.Given(Request.Create()
                .WithPath("/api/review/sessions/abc-123/transcript")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBody("""{"events":[]}"""));

        using var http = new HttpClient();

        var result = await McpJudgeServer.HandleToolCallForTests(
            toolName: "get_transcript",
            arguments: new JsonObject { ["session_id"] = "abc-123" },
            client: http,
            baseUrl: _server.Url!,
            expectedSessionId: "abc-123"
        );

        using var doc = JsonDocument.Parse(result);

        await Assert.That(
                doc.RootElement.GetProperty("result")
                    .GetProperty("content")[0]
                    .GetProperty("text")
                    .GetString()
            )
            .IsEqualTo("""{"events":[]}""");
    }

    [Test]
    public async Task get_transcript_forwards_file_path_skip_take_pagination() {
        _server.Given(Request.Create()
                .WithPath("/api/review/sessions/abc-123/transcript")
                .WithParam("file_path", "src/foo.cs")
                .WithParam("skip", "50")
                .WithParam("take", "25")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBody("""{"events":[{"id":51}]}"""));

        using var http = new HttpClient();

        var result = await McpJudgeServer.HandleToolCallForTests(
            toolName: "get_transcript",
            arguments: new JsonObject {
                ["session_id"] = "abc-123",
                ["file_path"]  = "src/foo.cs",
                ["skip"]       = 50,
                ["take"]       = 25
            },
            client: http,
            baseUrl: _server.Url!,
            expectedSessionId: "abc-123"
        );

        using var doc = JsonDocument.Parse(result);

        await Assert.That(
                doc.RootElement.GetProperty("result")
                    .GetProperty("content")[0]
                    .GetProperty("text")
                    .GetString()
            )
            .IsEqualTo("""{"events":[{"id":51}]}""");
    }

    [Test]
    public async Task get_transcript_errors_when_session_id_missing() {
        using var http = new HttpClient();

        var result = await McpJudgeServer.HandleToolCallForTests(
            toolName: "get_transcript",
            arguments: new JsonObject(),
            client: http,
            baseUrl: _server.Url!,
            expectedSessionId: "abc-123"
        );

        using var doc = JsonDocument.Parse(result);
        var content = doc.RootElement.GetProperty("result");

        await Assert.That(content.GetProperty("isError").GetBoolean()).IsTrue();
        await Assert.That(content.GetProperty("content")[0].GetProperty("text").GetString())
            .Contains("session_id");
    }

    [Test]
    public async Task get_transcript_rejects_mismatched_session_id() {
        using var http = new HttpClient();

        var result = await McpJudgeServer.HandleToolCallForTests(
            toolName: "get_transcript",
            arguments: new JsonObject { ["session_id"] = "other-session" },
            client: http,
            baseUrl: _server.Url!,
            expectedSessionId: "abc-123"
        );

        using var doc = JsonDocument.Parse(result);
        var content = doc.RootElement.GetProperty("result");

        await Assert.That(content.GetProperty("isError").GetBoolean()).IsTrue();
        await Assert.That(content.GetProperty("content")[0].GetProperty("text").GetString())
            .Contains("does not match this judge's bound session");
    }

    [Test]
    public async Task get_session_recap_errors_when_session_id_missing() {
        using var http = new HttpClient();

        var result = await McpJudgeServer.HandleToolCallForTests(
            toolName: "get_session_recap",
            arguments: new JsonObject(),
            client: http,
            baseUrl: _server.Url!,
            expectedSessionId: "abc-123"
        );

        using var doc = JsonDocument.Parse(result);
        var content = doc.RootElement.GetProperty("result");

        await Assert.That(content.GetProperty("isError").GetBoolean()).IsTrue();
        await Assert.That(content.GetProperty("content")[0].GetProperty("text").GetString())
            .Contains("session_id");
    }

    [Test]
    public async Task get_session_summary_forwards_session_id() {
        _server.Given(Request.Create()
                .WithPath("/api/sessions/abc-123/eval-summary")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBody("""{"session_id":"abc-123","title":"t","turn_count":5}"""));

        using var http = new HttpClient();
        var result = await McpJudgeServer.HandleToolCallForTests(
            toolName: "get_session_summary",
            arguments: new JsonObject { ["session_id"] = "abc-123" },
            client: http,
            baseUrl: _server.Url!,
            expectedSessionId: "abc-123"
        );

        using var doc = JsonDocument.Parse(result);
        var text = doc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        await Assert.That(text).Contains("\"turn_count\":5");
    }

    [Test]
    public async Task search_session_posts_query_body() {
        _server.Given(Request.Create()
                .WithPath("/api/sessions/abc-123/search")
                .UsingPost()
                .WithBody(b => b!.Contains("\"query\"")))
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBody("""{"session_id":"abc-123","query":"rm","total_candidates":0,"results":[]}"""));

        using var http = new HttpClient();
        var result = await McpJudgeServer.HandleToolCallForTests(
            toolName: "search_session",
            arguments: new JsonObject { ["session_id"] = "abc-123", ["query"] = "rm" },
            client: http,
            baseUrl: _server.Url!,
            expectedSessionId: "abc-123"
        );

        using var doc = JsonDocument.Parse(result);
        var text = doc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        await Assert.That(text).Contains("\"results\"");
    }

    [Test]
    public async Task get_tool_result_forwards_call_id_in_path() {
        _server.Given(Request.Create()
                .WithPath("/api/sessions/abc-123/tool-results/toolu_xyz")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBody("""{"call_id":"toolu_xyz","output":"untruncated-body"}"""));

        using var http = new HttpClient();
        var result = await McpJudgeServer.HandleToolCallForTests(
            toolName: "get_tool_result",
            arguments: new JsonObject { ["session_id"] = "abc-123", ["call_id"] = "toolu_xyz" },
            client: http,
            baseUrl: _server.Url!,
            expectedSessionId: "abc-123"
        );

        using var doc = JsonDocument.Parse(result);
        var text = doc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        await Assert.That(text).Contains("untruncated-body");
    }
}
