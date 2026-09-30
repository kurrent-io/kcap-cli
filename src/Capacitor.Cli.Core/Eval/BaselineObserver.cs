using System.Security.Cryptography;
using System.Text;
using Capacitor.Cli.Core.Eval.Contracts;
using Capacitor.Cli.Core.Eval.Evidence;

namespace Capacitor.Cli.Core.Eval;

/// <summary>Decorates an <see cref="IEvalObserver"/> to collect each question's and the
/// retrospective's judge usage, then writes a <see cref="BaselineOutput"/> to <paramref name="path"/>
/// on the run's one terminal callback (<see cref="OnFinished"/> or <see cref="OnFailed"/>), so a
/// baseline is still written for a partially-completed run.</summary>
public sealed class BaselineObserver(
        IEvalObserver inner,
        string        path,
        string        sessionId,
        string        model,
        bool          chain,
        TimeProvider  time
    ) : IEvalObserver {
    readonly List<BaselineQuestionOutput>  _questions = [];
    readonly List<BaselineQuestionFailure> _failures  = [];
    readonly long                          _started   = time.GetTimestamp();
    BaselineRetrospectiveOutput?           _retrospective;
    string                                 _evalRunId = "";
    EvalTreatment?                         _treatment;

    /// <summary>True once a terminal callback tried and failed to write the baseline file, so the
    /// caller can exit non-zero instead of reporting a success that produced no baseline.</summary>
    public bool WriteFailed { get; private set; }

    public void OnInfo(string message) => inner.OnInfo(message);

    public void OnStarted(string evalRunId, string judgeModel, int totalQuestions) {
        _evalRunId = evalRunId;
        inner.OnStarted(evalRunId, judgeModel, totalQuestions);
    }

    public void OnContextFetched(int traceEntries, int traceChars, int toolResultsTotal, int toolResultsTruncated, long bytesSaved) =>
        inner.OnContextFetched(traceEntries, traceChars, toolResultsTotal, toolResultsTruncated, bytesSaved);

    public void OnQuestionStarted(int index, int total, string category, string questionId) =>
        inner.OnQuestionStarted(index, total, category, questionId);

    public void OnQuestionCompleted(int index, int total, EvalQuestionAssessment assessment, EvalUsage usage, string route, TimeSpan elapsed, int runnerInvocations) {
        _questions.Add(new BaselineQuestionOutput {
            QuestionId = assessment.QuestionId,
            Route      = route,
            Usage      = usage,
            Calls      = runnerInvocations,
            ElapsedMs  = (long)elapsed.TotalMilliseconds
        });
        inner.OnQuestionCompleted(index, total, assessment, usage, route, elapsed, runnerInvocations);
    }

    public void OnQuestionFailed(int index, int total, string category, string questionId, string reason) {
        _failures.Add(new BaselineQuestionFailure {
            QuestionId = questionId,
            Category   = category,
            Reason     = reason
        });
        inner.OnQuestionFailed(index, total, category, questionId, reason);
    }

    public void OnFactRetained(string category, string fact) => inner.OnFactRetained(category, fact);

    public void OnRetrospectiveStarted() => inner.OnRetrospectiveStarted();

    public void OnRetrospectiveCompleted(EvalRetrospectiveV2 retrospective, EvalUsage usage, TimeSpan elapsed) {
        _retrospective = new BaselineRetrospectiveOutput {
            Usage     = usage,
            Calls     = 1,
            ElapsedMs = (long)elapsed.TotalMilliseconds
        };
        inner.OnRetrospectiveCompleted(retrospective, usage, elapsed);
    }

    public void OnRetrospectiveFailed(string reason) => inner.OnRetrospectiveFailed(reason);

    public void OnFinished(SessionEvalCompletedPayloadV4 aggregate) {
        WriteBaseline();
        inner.OnFinished(aggregate);
    }

    public void OnFailed(string reason) {
        WriteBaseline();
        inner.OnFailed(reason);
    }

    public void OnTreatment(EvalTreatment treatment) {
        _treatment = treatment;
        inner.OnTreatment(treatment);
    }

    public void OnQuestionLedger(int index, string questionId, string tempLedgerPath) {
        var copied = CopyLedger(index, questionId, tempLedgerPath);
        var at     = _questions.FindLastIndex(q => q.QuestionId == questionId && q.LedgerPath is null);
        if (at >= 0) _questions[at] = _questions[at] with { LedgerPath = copied };
        inner.OnQuestionLedger(index, questionId, tempLedgerPath);
    }

    // The question id is bounded only by length, so it never names a file: the ordinal keeps names distinct and the hash
    // ties the copy to its question, whose id the ledger's header carries.
    string? CopyLedger(int index, string questionId, string source) {
        var directory = Path.GetFullPath(path + ".ledgers");
        var hash      = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(questionId)))[..16];
        var target    = Path.GetFullPath(Path.Combine(directory, $"{index:D3}-{hash}.jsonl"));
        if (!target.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal)) {
            inner.OnInfo($"refused to retain the ledger for question {index}: it would land outside {directory}");
            return null;
        }
        try {
            Directory.CreateDirectory(directory);
            File.Copy(source, target, overwrite: true);
            return target;
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            inner.OnInfo($"failed to retain the ledger for question {index}: {e.Message}");
            return null;
        }
    }

    void WriteBaseline() {
        var usages = new List<EvalUsage>(_questions.Select(q => q.Usage));
        if (_retrospective is not null) usages.Add(_retrospective.Usage);

        var output = new BaselineOutput {
            SessionId      = sessionId,
            EvalRunId      = _evalRunId,
            Model          = model,
            Chain          = chain,
            Questions      = _questions,
            Failures       = _failures,
            Retrospective  = _retrospective,
            Totals         = EvalUsage.Sum(usages),
            TotalElapsedMs = (long)time.GetElapsedTime(_started).TotalMilliseconds,
            Treatment      = _treatment
        };

        try {
            BaselineOutputWriter.Write(path, output);
        } catch (Exception ex) {
            // Throwing here would rob the inner observer of its one terminal callback, and swallowing
            // the failure would report a baseline that was never written — flag it so the run fails.
            WriteFailed = true;
            inner.OnInfo($"failed to write baseline output to '{path}': {ex.Message}");
        }
    }
}
