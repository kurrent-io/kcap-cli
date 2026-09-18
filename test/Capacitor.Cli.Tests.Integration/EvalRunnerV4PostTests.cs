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
    readonly WireMockServer _server = WireMockServer.Start();
    public void Dispose() => _server.Stop();

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
        public void OnQuestionCompleted(int i, int t, EvalQuestionAssessment v, long it, long ot) { }
        public void OnQuestionFailed(int i, int t, string c, string q, string r) { }
        public void OnFactRetained(string c, string f) { }
        public void OnRetrospectiveStarted() { }
        public void OnRetrospectiveCompleted(EvalRetrospectiveV2 r) { }
        public void OnRetrospectiveFailed(string r) { }
        public void OnFinished(SessionEvalCompletedPayloadV4 a) { }
        public void OnFailed(string r) { }
    }
}
