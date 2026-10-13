namespace Capacitor.Cli.Commands;

/// <summary>One <see cref="ForegroundImportOutcome"/> from the per-level passes of a browser import.</summary>
internal static class ForegroundImportMerge {
    /// <summary>Only levels that ran belong in <paramref name="levels"/>, in run order: a level the answer
    /// did not choose is absent, not unknown. A level with null candidates ran without enumerating them.</summary>
    /// <returns><c>CohortOverride</c> is <see cref="HandoffCohort.PartialExact"/> when some levels are
    /// known and some not, whatever the id count, so the watch never treats half a cohort as all of it.
    /// <c>Remaining</c> counts candidates not yet landed, null when any level is unknown.</returns>
    public static (ForegroundImportOutcome Merged, HandoffCohort? CohortOverride, int? Remaining)
            Merge(IReadOnlyList<ForegroundImportOutcome> levels) {
        var known   = levels.Where(l => l.RunCandidateIds is not null).ToList();
        var unknown = levels.Count - known.Count;

        var merged = new ForegroundImportOutcome(
            levels.All(l => l.Certainty == ForegroundCertainty.Complete)
                ? ForegroundCertainty.Complete
                : ForegroundCertainty.Incomplete,
            levels.Sum(l => l.Selected),
            levels.Sum(l => l.Succeeded),
            levels.Sum(l => l.Skipped),
            levels.Sum(l => l.Failed),
            levels.Any(l => l.RemainderExists),
            known.Count == 0 ? null : [.. known.SelectMany(l => l.RunCandidateIds!)],
            [.. levels.SelectMany(l => l.SucceededIds)]);

        var cohort    = known.Count > 0 && unknown > 0 ? HandoffCohort.PartialExact : (HandoffCohort?)null;
        int? remaining = unknown > 0
            ? null
            : known.Sum(l => Math.Max(0, l.RunCandidateIds!.Count - l.SucceededIds.Count));

        return (merged, cohort, remaining);
    }
}
