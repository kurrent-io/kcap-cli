using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class SessionEvalsToolTests {
    static JsonObject Args(params JsonNode?[] ids) => new() { ["session_ids"] = new JsonArray(ids) };

    [Test]
    public async Task ParseSessionIds_dedupes_and_keeps_request_order() {
        var ids = SessionEvalsTool.ParseSessionIds(Args("b", "a", "b"));

        await Assert.That(string.Join(",", ids)).IsEqualTo("b,a");
    }

    [Test]
    [Arguments("a'b")]
    [Arguments("a b")]
    [Arguments("a;b")]
    [Arguments("")]
    public async Task ParseSessionIds_rejects_an_id_outside_the_grammar(string id) {
        await Assert.That(() => SessionEvalsTool.ParseSessionIds(Args(id))).Throws<ArgumentException>();
    }

    [Test]
    public async Task ParseSessionIds_rejects_missing_empty_oversized_and_non_string_input() {
        await Assert.That(() => SessionEvalsTool.ParseSessionIds(null)).Throws<ArgumentException>();
        await Assert.That(() => SessionEvalsTool.ParseSessionIds(Args())).Throws<ArgumentException>();
        await Assert.That(() => SessionEvalsTool.ParseSessionIds(Args(42))).Throws<ArgumentException>();
        await Assert.That(() => SessionEvalsTool.ParseSessionIds(Args(new string('a', 129)))).Throws<ArgumentException>();

        var tooMany = Enumerable.Range(0, SessionEvalsTool.MaxSessions + 1).Select(i => (JsonNode?)$"s{i}").ToArray();
        await Assert.That(() => SessionEvalsTool.ParseSessionIds(Args(tooMany))).Throws<ArgumentException>();
    }

    [Test]
    public async Task Project_maps_route_statuses_to_states() {
        await Assert.That(State(SessionEvalsTool.Project("s", HttpStatusCode.NotFound, null))).IsEqualTo("not_found");
        await Assert.That(State(SessionEvalsTool.Project("s", HttpStatusCode.NoContent, null))).IsEqualTo("not_evaluated");

        var error = SessionEvalsTool.Project("s", HttpStatusCode.InternalServerError, null);
        await Assert.That(State(error)).IsEqualTo("error");
        await Assert.That(error["http_status"]!.GetValue<int>()).IsEqualTo(500);

        await Assert.That(State(SessionEvalsTool.Project("s", HttpStatusCode.OK, "not json"))).IsEqualTo("error");
    }

    [Test]
    public async Task Project_reads_a_live_run_as_running_with_progress() {
        const string body = """
            {"eval_run_id":"r1","is_terminal":false,"total_questions":3,"queue_position":null,"completed_result":null,
             "questions":[{"status":"completed"},{"status":"failed"},{"status":"running"}]}
            """;

        var entry = SessionEvalsTool.Project("s", HttpStatusCode.OK, body);

        await Assert.That(State(entry)).IsEqualTo("running");
        await Assert.That(entry["questions_done"]!.GetValue<int>()).IsEqualTo(2);
        await Assert.That(entry["total_questions"]!.GetValue<long>()).IsEqualTo(3);
    }

    [Test]
    public async Task Project_reads_a_terminal_run_without_a_result_as_failed() {
        var entry = SessionEvalsTool.Project("s", HttpStatusCode.OK,
            """{"eval_run_id":"r1","is_terminal":true,"failure_reason":"judge timed out","completed_result":null}""");

        await Assert.That(State(entry)).IsEqualTo("failed");
        await Assert.That(entry["failure_reason"]!.GetValue<string>()).IsEqualTo("judge timed out");
    }

    /// <summary>A not-applicable answer scores lowest here, so it is what would be reported as weakest
    /// if outcome were ignored.</summary>
    [Test]
    public async Task Project_reads_a_completed_run_and_picks_the_weakest_assessed_questions() {
        const string body = """
            {"eval_run_id":"r1","is_terminal":true,
             "questions":[
               {"question_id":"q1","category":"safety","score":1,"outcome":"not_applicable"},
               {"question_id":"q2","category":"safety","score":4,"outcome":"assessed"},
               {"question_id":"q3","category":"planning","score":2,"outcome":"assessed"},
               {"question_id":"q4","category":"planning","score":3,"outcome":"assessed"}],
             "completed_result":{"eval_run_id":"r1","judge_model":"m","evaluated_at":"2026-10-07T10:00:00Z","overall_score":4,"summary":"Good.",
               "categories":[{"name":"safety","score":4,"questions":[]},{"name":"planning","score":3,"questions":[]}]}}
            """;

        var entry = SessionEvalsTool.Project("s", HttpStatusCode.OK, body);

        await Assert.That(State(entry)).IsEqualTo("completed");
        await Assert.That(entry["overall_score"]!.GetValue<long>()).IsEqualTo(4);
        await Assert.That(entry["evaluated_at"]!.GetValue<string>()).IsEqualTo("2026-10-07T10:00:00Z");
        await Assert.That(entry["categories"]!.AsArray().Count).IsEqualTo(2);
        await Assert.That(Weakest(entry)).IsEqualTo("q3,q4");
    }

    [Test]
    public async Task Project_falls_back_to_category_verdicts_when_the_run_kept_no_question_list() {
        const string body = """
            {"eval_run_id":"r1","is_terminal":true,"questions":[],
             "completed_result":{"eval_run_id":"r1","judge_model":"m","evaluated_at":"2026-10-07T10:00:00Z","overall_score":3,"summary":"",
               "categories":[{"name":"safety","score":3,"questions":[
                 {"category":"safety","question_id":"q9","score":5},{"category":"safety","question_id":"q8","score":2}]}]}}
            """;

        await Assert.That(Weakest(SessionEvalsTool.Project("s", HttpStatusCode.OK, body))).IsEqualTo("q8,q9");
    }

    [Test]
    public async Task FetchAsync_answers_every_id_in_request_order() {
        var handler = new RoutingHandler(path => path switch {
            "/api/sessions/a/eval-progress" => (HttpStatusCode.NoContent, ""),
            "/api/sessions/b/eval-progress" => (HttpStatusCode.NotFound, ""),
            _                               => throw new HttpRequestException("connection refused")
        });

        var result = await SessionEvalsTool.FetchAsync(new HttpClient(handler), "http://srv", ["a", "b", "c"], new FakeTimeProvider());

        var sessions = result!["sessions"]!.AsArray();
        await Assert.That(string.Join(",", sessions.Select(s => s!["session_id"]!.GetValue<string>()))).IsEqualTo("a,b,c");
        await Assert.That(string.Join(",", sessions.Select(s => State(s!.AsObject())))).IsEqualTo("not_evaluated,not_found,error");
    }

    [Test]
    public async Task FetchAsync_returns_null_when_the_credential_is_rejected() {
        var handler = new RoutingHandler(path => path.Contains("/b/") ? (HttpStatusCode.Unauthorized, "") : (HttpStatusCode.NoContent, ""));

        await Assert.That(await SessionEvalsTool.FetchAsync(new HttpClient(handler), "http://srv", ["a", "b"], new FakeTimeProvider())).IsNull();
    }

    [Test]
    public async Task Sessions_server_lists_the_tool() {
        await Assert.That(McpSessionsServer.BuildToolsList().Select(t => t.Name)).Contains(SessionEvalsTool.Name);
    }

    static string State(JsonObject entry) => entry["state"]!.GetValue<string>();

    static string Weakest(JsonObject entry) =>
        string.Join(",", entry["weakest_questions"]!.AsArray().Select(q => q!["question_id"]!.GetValue<string>()));

    sealed class RoutingHandler(Func<string, (HttpStatusCode Status, string Body)> route) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            var (status, body) = route(request.RequestUri!.AbsolutePath);

            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
