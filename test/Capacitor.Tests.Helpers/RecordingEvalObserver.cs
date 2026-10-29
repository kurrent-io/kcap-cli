using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Eval;
using Capacitor.Cli.Core.Eval.Contracts;
using Capacitor.Cli.Core.Eval.Evidence;

namespace Capacitor.Tests.Helpers;

/// <summary>Records every eval callback it receives, for assertions across the eval suites.</summary>
public sealed class RecordingEvalObserver : IEvalObserver {
    readonly Lock _gate = new();

    public List<string> Info { get; } = [];
    public List<string> Started { get; } = [];
    public List<(int Index, EvalQuestionAssessment Assessment, string Route)> Completed { get; } = [];
    public List<(int Index, string QuestionId, string Reason)> Failed { get; } = [];
    public List<(string Category, string Fact)> FactsRetained { get; } = [];
    public List<string> Failures { get; } = [];
    public SessionEvalCompletedPayloadV4? Finished { get; private set; }
    public EvalTreatment? Treatment { get; private set; }
    public List<(int Index, string QuestionId, string Path)> Ledgers { get; } = [];

    public void OnInfo(string message) { lock (_gate) Info.Add(message); }
    public void OnStarted(string evalRunId, string judgeModel, int totalQuestions) { lock (_gate) Started.Add(evalRunId); }
    public void OnContextFetched(int traceEntries, int traceChars, int toolResultsTotal, int toolResultsTruncated, long bytesSaved) { }
    public void OnQuestionStarted(int index, int total, string category, string questionId) { }
    public void OnQuestionCompleted(int index, int total, EvalQuestionAssessment assessment, EvalUsage usage, string route, TimeSpan elapsed, int runnerInvocations) { lock (_gate) Completed.Add((index, assessment, route)); }
    public void OnQuestionFailed(int index, int total, string category, string questionId, string reason) { lock (_gate) Failed.Add((index, questionId, reason)); }
    public void OnFactRetained(string category, string fact) { lock (_gate) FactsRetained.Add((category, fact)); }
    public void OnRetrospectiveStarted() { }
    public void OnRetrospectiveCompleted(EvalRetrospectiveV2 retrospective, EvalUsage usage, TimeSpan elapsed) { }
    public void OnRetrospectiveFailed(string reason) { lock (_gate) Info.Add($"retrospective failed: {reason}"); }
    public void OnFinished(SessionEvalCompletedPayloadV4 aggregate) { lock (_gate) Finished = aggregate; }
    public void OnFailed(string reason) { lock (_gate) Failures.Add(reason); }
    public void OnTreatment(EvalTreatment treatment) { lock (_gate) Treatment = treatment; }
    public void OnQuestionLedger(int index, string questionId, string tempLedgerPath) { lock (_gate) Ledgers.Add((index, questionId, tempLedgerPath)); }
}
