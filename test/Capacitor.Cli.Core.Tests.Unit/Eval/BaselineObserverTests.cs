using System.Text.Json;
using Capacitor.Cli.Core.Eval;
using Capacitor.Cli.Core.Eval.Contracts;

namespace Capacitor.Cli.Core.Tests.Unit.Eval;

public class BaselineObserverTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task Records_completed_and_failed_questions_and_still_calls_the_inner_terminal() {
        var inner = new RecordingObserver();
        var path  = Tmp.PathTo("baseline.json");
        var sut   = new BaselineObserver(inner, path, "sess-1", "sonnet", chain: false, TimeProvider.System);

        sut.OnStarted("run-1", "sonnet", 2);
        sut.OnQuestionCompleted(
            1, 2,
            new EvalQuestionAssessment { Category = "safety", QuestionId = "ok_q", Finding = "fine" },
            new EvalUsage { InputTokens = 10, OutputTokens = 5 },
            "text", TimeSpan.FromMilliseconds(120), runnerInvocations: 1);
        sut.OnQuestionFailed(2, 2, "safety", "failed_q", "null judge result");
        sut.OnFinished(Aggregate());

        await Assert.That(sut.WriteFailed).IsFalse();
        await Assert.That(inner.FinishedCount).IsEqualTo(1);

        using var doc  = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var       root = doc.RootElement;

        var questions = root.GetProperty("questions");
        await Assert.That(questions.GetArrayLength()).IsEqualTo(1);
        await Assert.That(questions[0].GetProperty("question_id").GetString()).IsEqualTo("ok_q");

        var failures = root.GetProperty("failures");
        await Assert.That(failures.GetArrayLength()).IsEqualTo(1);
        await Assert.That(failures[0].GetProperty("question_id").GetString()).IsEqualTo("failed_q");
        await Assert.That(failures[0].GetProperty("category").GetString()).IsEqualTo("safety");
        await Assert.That(failures[0].GetProperty("reason").GetString()).IsEqualTo("null judge result");
    }

    [Test]
    public async Task A_failed_write_is_surfaced_rather_than_reported_as_success() {
        var inner   = new RecordingObserver();
        var blocker = Tmp.CreateFile("blocker.txt", "x");
        // A path whose parent component is a regular file cannot be written to on any platform.
        var badPath = Path.Combine(blocker, "baseline.json");
        var sut     = new BaselineObserver(inner, badPath, "sess-1", "sonnet", chain: false, TimeProvider.System);

        sut.OnFinished(Aggregate());

        await Assert.That(sut.WriteFailed).IsTrue();
        await Assert.That(inner.FinishedCount).IsEqualTo(1);
        await Assert.That(inner.Info.Any(m => m.Contains("failed to write baseline output"))).IsTrue();
    }

    static SessionEvalCompletedPayloadV4 Aggregate() => new() {
        EvalRunId             = "run-1",
        JudgeModel            = "sonnet",
        Summary               = "done",
        AssessedQuestions     = 1,
        UnassessedQuestions   = 0,
        JudgedQuestions       = 1,
        TotalQuestions        = 2,
        CoveragePolicyVersion = "v1",
    };

    sealed class RecordingObserver : IEvalObserver {
        public List<string> Info          { get; } = [];
        public int          FinishedCount { get; private set; }
        public int          FailedCount   { get; private set; }

        public void OnInfo(string message) => Info.Add(message);
        public void OnStarted(string evalRunId, string judgeModel, int totalQuestions) { }
        public void OnContextFetched(int traceEntries, int traceChars, int toolResultsTotal, int toolResultsTruncated, long bytesSaved) { }
        public void OnQuestionStarted(int index, int total, string category, string questionId) { }
        public void OnQuestionCompleted(int index, int total, EvalQuestionAssessment assessment, EvalUsage usage, string route, TimeSpan elapsed, int runnerInvocations) { }
        public void OnQuestionFailed(int index, int total, string category, string questionId, string reason) { }
        public void OnFactRetained(string category, string fact) { }
        public void OnRetrospectiveStarted() { }
        public void OnRetrospectiveCompleted(EvalRetrospectiveV2 retrospective, EvalUsage usage, TimeSpan elapsed) { }
        public void OnRetrospectiveFailed(string reason) { }
        public void OnFinished(SessionEvalCompletedPayloadV4 aggregate) => FinishedCount++;
        public void OnFailed(string reason) => FailedCount++;
    }
}
