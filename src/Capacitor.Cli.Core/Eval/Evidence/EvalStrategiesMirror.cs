namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>The one strategy rule a producer needs from the server: which question of a run reports the completion checklist.</summary>
public static class EvalStrategiesMirror {
    public const string Completion = "completion";

    /// <summary>Among the run's completion questions, the one the catalog marks, else the first in run order; none without one.
    /// Only the exact id counts: an id this build does not know behaves as general.</summary>
    public static string? ReportingQuestion(IReadOnlyList<EvalQuestionDto> questions) {
        string? first = null;
        foreach (var q in questions) {
            if (q.Strategy != Completion) continue;
            if (q.ReportsObligations) return q.Id;
            first ??= q.Id;
        }
        return first;
    }
}
