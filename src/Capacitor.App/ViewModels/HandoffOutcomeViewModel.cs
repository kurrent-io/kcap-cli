using Capacitor.Cli.Core.Handoff;

namespace Capacitor.App.ViewModels;

/// What a hand-off carried over from the previous session, worded for the card above the transcript.
public sealed class HandoffOutcomeViewModel {
    public const string RunningNote = "Carrying over work items and plans…";
    public const string SourceActiveNote = "The previous session is active again, so its work items and plans stayed with it.";
    public const string InterruptedNote = "The hand-off was cut short before its work items and plans were carried over.";

    public HandoffOutcomeViewModel(HandoffOutcomeDto outcome) {
        Header = $"Continued from {(string.IsNullOrWhiteSpace(outcome.SourceTitle) ? outcome.SourceSessionId : outcome.SourceTitle)}";
        Note = NoteFor(outcome);
        NoteNeedsAttention = outcome.State is "refused" or "failed" or "unauthorized" or "interrupted";
        Rows = [.. outcome.Items.Select(HandoffOutcomeRow.From)];
    }

    public string Header { get; }
    public string? Note { get; }
    public bool HasNote => Note is not null;
    public bool NoteNeedsAttention { get; }
    public IReadOnlyList<HandoffOutcomeRow> Rows { get; }

    static string? NoteFor(HandoffOutcomeDto outcome) => outcome switch {
        { State: "running" }                            => RunningNote,
        { State: "interrupted" }                        => InterruptedNote,
        { State: "refused", Detail: "source_active" }   => SourceActiveNote,
        { State: "refused" or "failed", Detail: { Length: > 0 } code } => Refusal(code),
        { State: "unauthorized" }                       => Refusal("unauthorized"),
        { State: "refused" or "failed" }                => "Couldn't carry over the previous session's work.",
        _                                               => null,
    };

    static string Refusal(string code) => $"Couldn't carry over the previous session's work ({code}).";
}
