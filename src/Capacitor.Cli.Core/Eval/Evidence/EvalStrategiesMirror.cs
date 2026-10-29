namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>The one strategy rule a producer needs from the server: which question of a run reports the completion checklist.</summary>
public static class EvalStrategiesMirror {
    public const string Completion = "completion";

    /// <summary>Among the run's completion questions, the one the catalog marks, else the first in run order; none without one.
    /// Only the exact id counts: an id this build does not know behaves as general.</summary>
    public static string? ReportingQuestion(IReadOnlyList<EvalQuestionDto> questions) =>
        ReportingQuestion(questions.Select(q => (q.Id, q.Strategy, q.ReportsObligations)));

    public static string? ReportingQuestion(IEnumerable<(string Id, string? Strategy, bool ReportsObligations)> questions) {
        string? first = null;
        foreach (var (id, strategy, reports) in questions) {
            if (strategy != Completion) continue;
            if (reports) return id;
            first ??= id;
        }
        return first;
    }

    /// <summary>The run's reporting question in the server's order: the catalog's, whatever order the run selected its
    /// questions in.</summary>
    public static string? ReportingQuestion(EvalCatalogDto catalog, IReadOnlyCollection<string> runIds) =>
        ReportingQuestion(catalog.Questions.Where(q => runIds.Contains(q.Id)).Select(q => (q.Id, q.Strategy, q.ReportsObligations == true)));
}
