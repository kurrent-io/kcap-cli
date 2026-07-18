using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Eval;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Phase 3: locks the daemon's V3 wire-format migration. The persistence
/// step must POST to /api/sessions/{id}/evals/v3 (not v2) and the body must
/// carry per-question prompt_version and retrospective_prompt_version.
/// Drives PersistAggregateV3Async directly (same seam as EvalRunnerV2PostTests).
/// </summary>
public class EvalRunnerV3PostTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();
    public void Dispose() => _server.Stop();

    [Test]
    public async Task Daemon_persists_aggregate_to_v3_route_with_versions() {
        _server.Given(Request.Create().WithPath("/api/sessions/sess-1/evals/v3").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(200));
        // v2 still wired but must NOT be hit.
        _server.Given(Request.Create().WithPath("/api/sessions/sess-1/evals/v2").UsingPost())
               .RespondWith(Response.Create().WithStatusCode(410));

        var aggregate = new SessionEvalCompletedPayloadV3 {
            EvalRunId    = "run-1",
            JudgeModel   = "claude-sonnet-4-6",
            OverallScore = 4,
            Summary      = "ok",
            RetrospectivePromptVersion = "1240",
            Categories   = [ new EvalCategoryResult { Name = "safety", Score = 5, Verdict = "pass",
                Questions = [ new EvalQuestionVerdict { Category = "safety", QuestionId = "k1",
                    Score = 5, Verdict = "pass", Finding = "ok", PromptVersion = "1234" } ] } ],
            Retrospective = new EvalRetrospectiveV2 {
                OverallSummary = "fine",
                Suggestions = [ new RetrospectiveSuggestion { Text = "Run tests", Audience = "agent" } ]
            }
        };

        var observer = new RecordingObserver();
        using var httpClient = new HttpClient();

        var ok = await EvalService.PersistAggregateV3Async(
            httpClient: httpClient, baseUrl: _server.Url!, encodedSessionId: "sess-1",
            aggregate: aggregate, observer: observer, ct: CancellationToken.None);

        await Assert.That(ok).IsTrue();

        var v3Hits = _server.FindLogEntries(Request.Create().WithPath("/api/sessions/sess-1/evals/v3").UsingPost());
        var v2Hits = _server.FindLogEntries(Request.Create().WithPath("/api/sessions/sess-1/evals/v2").UsingPost());
        await Assert.That(v3Hits.Count).IsEqualTo(1);
        await Assert.That(v2Hits.Count).IsEqualTo(0);

        var body = v3Hits[0].RequestMessage.Body!;
        await Assert.That(body).Contains(@"""eval_run_id"":""run-1""");
        await Assert.That(body).Contains(@"""prompt_version"":""1234""");
        await Assert.That(body).Contains(@"""retrospective_prompt_version"":""1240""");
    }

    sealed class RecordingObserver : IEvalObserver {
        public void OnInfo(string m) { }
        public void OnStarted(string r, string j, int t) { }
        public void OnContextFetched(int a, int b, int c, int d, long e) { }
        public void OnQuestionStarted(int i, int t, string c, string q) { }
        public void OnQuestionCompleted(int i, int t, EvalQuestionVerdict v, long it, long ot) { }
        public void OnQuestionFailed(int i, int t, string c, string q, string r) { }
        public void OnFactRetained(string c, string f) { }
        public void OnRetrospectiveStarted() { }
        public void OnRetrospectiveCompleted(EvalRetrospectiveV2 r) { }
        public void OnRetrospectiveFailed(string r) { }
        public void OnFinished(SessionEvalCompletedPayloadV3 a) { }   // V3 after Task 5
        public void OnFailed(string r) { }
    }
}
