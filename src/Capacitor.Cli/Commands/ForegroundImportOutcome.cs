namespace Capacitor.Cli.Commands;

/// <summary>What setup knows after the foreground pass. Incomplete carries no counts: a pass that
/// did not finish reported no partition, and everything selected is treated as not landed.</summary>
internal sealed record ForegroundImportOutcome(
        ForegroundCertainty    Certainty,
        int                    Selected,
        int                    Succeeded,
        int                    Skipped,
        int                    Failed,
        bool                   RemainderExists,
        IReadOnlyList<string>? RunCandidateIds,
        IReadOnlyList<string>  SucceededIds) {

    public static ForegroundImportOutcome From(SetupImportRun run) {
        var complete  = run.Fault is null && run.Outcome is not null && run.Selection is not null;
        var partition = complete ? run.Outcome!.Partition ?? ImportRunPartition.Empty : ImportRunPartition.Empty;

        return new ForegroundImportOutcome(
            complete ? ForegroundCertainty.Complete : ForegroundCertainty.Incomplete,
            run.Selection?.SelectedIds.Count ?? 0,
            partition.SucceededIds.Count,
            partition.SkippedIds.Count,
            partition.FailedIds.Count,
            run.Selection?.RemainderExists ?? true,
            run.Selection?.RunCandidateIds,
            partition.SucceededIds);
    }
}
