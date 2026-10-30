namespace Capacitor.App.ViewModels;

/// How many of the work item's parts carry one mark, as the collapsed PARTS header shows it.
public sealed record WorkContextPartCount(WorkContextPartMark Mark, int Count) {
    /// The count in words, for the tooltip: the mark beside the number carries no text.
    public string Label => Mark switch {
        WorkContextPartMark.ThisSession => $"{Count} in this session",
        WorkContextPartMark.Settled     => $"{Count} done",
        _                               => $"{Count} open",
    };
}
