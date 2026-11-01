using Capacitor.Cli.Core.Handoff;

namespace Capacitor.App.ViewModels;

/// What a hand-off carried over from the previous session, worded for the card above the transcript.
public sealed class HandoffOutcomeViewModel {
    public const string RunningNote = "Carrying over work items and plans…";
    public const string SourceActiveNote = "The previous session is active again, so its work items and plans stayed with it.";
    public const string SignInNote = "Your kcap sign-in was rejected, so the previous session's work wasn't carried over. Sign in again before your next hand-off.";
    public const string SignInPartlyNote = "Your kcap sign-in was rejected partway, so only some of the previous session's work was carried over. Sign in again before your next hand-off.";
    public const string InterruptedNote = "The hand-off was cut short before its work items and plans were carried over.";

    public HandoffOutcomeViewModel(HandoffOutcomeDto outcome) {
        Header = $"Continued from {(string.IsNullOrWhiteSpace(outcome.SourceTitle) ? outcome.SourceSessionId : HandoffOutcomeRow.Fold(outcome.SourceTitle))}";
        Note = NoteFor(outcome);
        NoteNeedsAttention = outcome.State is "failed" or "unauthorized" or "interrupted"
            || outcome is { State: "refused", Detail: not "source_active" };
        Rows = [.. outcome.Items.Select(HandoffOutcomeRow.From)];
    }

    public string Header { get; }
    public string? Note { get; }
    public bool HasNote => Note is not null;
    public bool NoteNeedsAttention { get; }
    public IReadOnlyList<HandoffOutcomeRow> Rows { get; }

    static string? NoteFor(HandoffOutcomeDto outcome) {
        var partly = outcome.Items.Any(item => item.Result == "carried");

        return outcome switch {
            { State: "running" }                          => RunningNote,
            { State: "interrupted" }                      => InterruptedNote,
            { State: "refused", Detail: "source_active" } => SourceActiveNote,
            { State: "unauthorized" }                     => partly ? SignInPartlyNote : SignInNote,
            { State: "failed", Detail: { Length: > 0 } code } when partly => $"Only part of the previous session's work was carried over ({code}).",
            { State: "refused" or "failed", Detail: { Length: > 0 } code } => Refusal(code),
            { State: "refused" or "failed" }              => "Couldn't carry over the previous session's work.",
            _                                             => null,
        };
    }

    static string Refusal(string code) => $"Couldn't carry over the previous session's work ({code}).";
}
