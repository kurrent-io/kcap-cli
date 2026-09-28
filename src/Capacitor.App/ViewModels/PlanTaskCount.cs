namespace Capacitor.App.ViewModels;

/// How many of the plan's tasks are in one state, as the collapsed sidebar section shows it.
/// Completed stands for every settled task, a skipped one included.
public sealed record PlanTaskCount(PlanTaskState State, int Count) {
    /// The count in words, for the tooltip: the mark beside the number carries no text.
    public string Label => State switch {
        PlanTaskState.InProgress => $"{Count} in progress",
        PlanTaskState.Completed  => $"{Count} done",
        _                        => $"{Count} pending",
    };
}
