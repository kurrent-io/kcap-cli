namespace Capacitor.App.ViewModels;

/// How many of a session's subagents present one state, as the collapsed sidebar section shows it.
public sealed record SubagentCount(SubagentState State, int Count) {
    /// The count in words, for the tooltip: the mark beside the number carries no text.
    public string Label => State switch {
        SubagentState.Running => $"{Count} running",
        SubagentState.Failed  => $"{Count} failed",
        SubagentState.Stopped => $"{Count} stopped",
        _                     => $"{Count} completed",
    };
}
