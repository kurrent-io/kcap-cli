using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Eval;
using Capacitor.Cli.Core.Eval.Contracts;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Locks the daemon's V4 wire-format migration: the persistence step must POST to
/// <c>/api/sessions/{id}/evals/v4</c> (not v3) and the body must carry outcomes, a null score for
/// an unassessed question, the coverage policy version, and coded failures.
/// </summary>
public class EvalRunnerV4PostTests : IDisposable {
    [TempHome] public required TempHome Home { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();
    public void Dispose() => _server.Stop();

    const string Context = """{"session_id":"sess-1","session_chain":["sess-1"],"trace":[{"kind":"user","timestamp":"2026-09-23T12:00:00Z","text":"hi"}],"compaction":{"threshold_bytes":2000,"entries":1,"tool_results_total":0,"tool_results_truncated":0,"bytes_saved":0,"plan_discovery_degraded":false,"skipped_streams":0,"plan_artifacts_truncated":0,"plan_artifacts_unavailable":0,"plan_artifacts_dropped":0}}""";

    EvalService.EvalContext Ctx() {
        var result = JsonSerializer.Deserialize(Context, CapacitorJsonContext.Default.EvalContextResult)!;
        return new("run-1", "sess-1", "sess-1", Context, result, result.Compaction, "tools", "Retro", "7",
            [new EvalQuestionDto { Category = "safety", Id = "k1", Text = "t", Prompt = "p" }, new EvalQuestionDto { Category = "safety", Id = "k2", Text = "t", Prompt = "p" }],
            "sonnet", false, null, TestHarnesses.Under(Home));
    }

    void RespondV4(int status) =>
        _server.Given(Request.Create().WithPath("/api/sessions/sess-1/evals/v4").UsingPost()).RespondWith(Response.Create().WithStatusCode(status));

    static readonly EvalQuestionFailure[] AllFailed = [
        new() { Category = "safety", QuestionId = "k1", Code = "judge_timeout" },
        new() { Category = "safety", QuestionId = "k2", Code = "iteration_cap", MaxIterations = 15, TurnsFetched = 1, TurnsTotal = 3 }
    ];

    const string FailureOnlyBody = """
        {"eval_run_id":"run-1","judge_model":"sonnet","categories":[],"overall_score":null,"summary":"Not evaluated: all 2 questions failed (iteration_cap \u00D71, judge_timeout \u00D71)","retrospective":null,"retrospective_prompt_version":null,"facts_used":[],"assessed_questions":0,"unassessed_questions":0,"judged_questions":0,"total_questions":2,"failed_questions":[{"category":"safety","question_id":"k1","code":"judge_timeout"},{"category":"safety","question_id":"k2","code":"iteration_cap","max_iterations":15,"turns_fetched":1,"turns_total":3}],"coverage_policy_version":"coverage-v1","evidence_scope_version":null}
        """;

    /// <summary>A run whose every question failed posts the failure-only record — no category, zero counts, null score,
    /// retrospective and prompt version, no facts — and ends in OnFailed with the code summary, never OnFinished.</summary>
    [Test]
    public async Task An_all_failed_run_posts_the_failure_only_record_and_reports_it_failed() {
        RespondV4(200);
        var observer = new RecordingEvalObserver();
        using var http = new HttpClient();

        var result = await EvalService.FinalizeAsync(Ctx(), http, _server.Url!, [], AllFailed, "sonnet", observer, TimeProvider.System, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result!.IsFailureOnly).IsTrue();
        await Assert.That(observer.Finished).IsNull();
        await Assert.That(observer.Failures).IsEquivalentTo(["Not evaluated: all 2 questions failed (iteration_cap ×1, judge_timeout ×1)"]);
        var hit = _server.FindLogEntries(Request.Create().WithPath("/api/sessions/sess-1/evals/v4").UsingPost()).Single();
        await Assert.That(hit.RequestMessage.Body).IsEqualTo(FailureOnlyBody);
    }

    [Test]
    public async Task A_server_that_rejects_the_failure_only_record_is_reported_and_nothing_is_returned() {
        RespondV4(400);
        var observer = new RecordingEvalObserver();
        using var http = new HttpClient();

        var result = await EvalService.FinalizeAsync(Ctx(), http, _server.Url!, [], AllFailed, "sonnet", observer, TimeProvider.System, CancellationToken.None);

        await Assert.That(result).IsNull();
        await Assert.That(observer.Finished).IsNull();
        await Assert.That(observer.Failures).IsEquivalentTo(["failed to persist eval result: HTTP 400"]);
    }

    [Test]
    public async Task A_run_with_neither_an_assessment_nor_a_failure_posts_nothing() {
        RespondV4(200);
        var observer = new RecordingEvalObserver();
        using var http = new HttpClient();

        var result = await EvalService.FinalizeAsync(Ctx(), http, _server.Url!, [], [], "sonnet", observer, TimeProvider.System, CancellationToken.None);

        await Assert.That(result).IsNull();
        await Assert.That(observer.Failures).IsEquivalentTo(["all judge invocations failed"]);
        await Assert.That(_server.LogEntries).IsEmpty();
    }

    [Test]
    public async Task Daemon_persists_aggregate_to_v4_route_with_outcomes_and_failures() {
        _server.Given(Request.Create().WithPath("/api/sessions/sess-1/evals/v4").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200));
        // v3 still wired but must NOT be hit.
        _server.Given(Request.Create().WithPath("/api/sessions/sess-1/evals/v3").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(410));

        var aggregate = new SessionEvalCompletedPayloadV4 {
            EvalRunId    = "run-1",
            JudgeModel   = "claude-sonnet-4-6",
            OverallScore = 5,
            Summary      = "ok",
            RetrospectivePromptVersion = "1240",
            AssessedQuestions          = 1,
            UnassessedQuestions        = 1,
            JudgedQuestions            = 2,
            TotalQuestions             = 3,
            CoveragePolicyVersion      = "coverage-v1",
            Categories = [
                new EvalCategoryAssessment {
                    Name = "safety", Score = 5, Verdict = "pass",
                    Questions = [
                        new EvalQuestionAssessment {
                            Category = "safety", QuestionId = "k1", Outcome = "assessed",
                            Score = 5, Verdict = "pass", Finding = "ok", PromptVersion = "1234"
                        },
                        new EvalQuestionAssessment {
                            Category = "safety", QuestionId = "k2", Outcome = "insufficient_evidence",
                            Score = null, Verdict = null, Finding = "trace was truncated"
                        }
                    ]
                }
            ],
            FailedQuestions = [
                new EvalQuestionFailure { Category = "safety", QuestionId = "k3", Code = "judge_timeout" }
            ]
        };

        var observer = new RecordingObserver();
        using var httpClient = new HttpClient();

        var ok = await EvalService.PersistAggregateV4Async(
            httpClient: httpClient, baseUrl: _server.Url!, encodedSessionId: "sess-1",
            aggregate: aggregate, observer: observer, ct: CancellationToken.None, time: TimeProvider.System);

        await Assert.That(ok).IsTrue();

        var v4Hits = _server.FindLogEntries(Request.Create().WithPath("/api/sessions/sess-1/evals/v4").UsingPost());
        var v3Hits = _server.FindLogEntries(Request.Create().WithPath("/api/sessions/sess-1/evals/v3").UsingPost());
        await Assert.That(v4Hits.Count).IsEqualTo(1);
        await Assert.That(v3Hits.Count).IsEqualTo(0);

        var body = v4Hits[0].RequestMessage.Body!;
        await Assert.That(body).Contains(@"""eval_run_id"":""run-1""");
        await Assert.That(body).Contains(@"""outcome"":""insufficient_evidence""");
        await Assert.That(body).Contains(@"""score"":null");
        await Assert.That(body).Contains(@"""coverage_policy_version"":""coverage-v1""");
        await Assert.That(body).Contains(@"""failed_questions""");
        await Assert.That(body).Contains(@"""judge_timeout""");
    }

    sealed class RecordingObserver : IEvalObserver {
        public void OnInfo(string m) { }
        public void OnStarted(string r, string j, int t) { }
        public void OnContextFetched(int a, int b, int c, int d, long e) { }
        public void OnQuestionStarted(int i, int t, string c, string q) { }
        public void OnQuestionCompleted(int i, int t, EvalQuestionAssessment v, EvalUsage u, string r, TimeSpan e, int c) { }
        public void OnQuestionFailed(int i, int t, string c, string q, string r) { }
        public void OnFactRetained(string c, string f) { }
        public void OnRetrospectiveStarted() { }
        public void OnRetrospectiveCompleted(EvalRetrospectiveV2 r, EvalUsage u, TimeSpan e) { }
        public void OnRetrospectiveFailed(string r) { }
        public void OnFinished(SessionEvalCompletedPayloadV4 a) { }
        public void OnFailed(string r) { }
    }
}
