namespace Capacitor.App.ViewModels;

/// How many of a session's subagents present one state, as the collapsed sidebar section shows it.
public sealed record RunCount(RunState State, int Count) {
    /// The count in words, for the tooltip: the mark beside the number carries no text.
    public string Label => State switch {
        RunState.Running => $"{Count} running",
        RunState.Failed  => $"{Count} failed",
        RunState.Stopped => $"{Count} stopped",
        _                     => $"{Count} completed",
    };
}
