using Avalonia.Media;

namespace Capacitor.App.ViewModels;

/// Pending has not started; Note is a non-blocking result, and Problem needs the user.
public enum UpdateStepState { Pending, Running, Done, Note, Problem }

/// One line of the update window: an app part and where it stands.
/// <param name="Trailing">A short figure kept apart from the detail, such as a download percentage, so the detail does not reflow as it changes.</param>
/// <param name="Hint">Full progress or diagnostic text; settled notes and problems also expose it through Details.</param>
/// <param name="Activity">What a running step is doing now, in plain words, for the launcher while the update finishes.</param>
public sealed record UpdateStep(string Title, string? Detail, UpdateStepState State, string? Trailing = null, string? Hint = null,
        string? Activity = null) {
    public bool IsPending => State == UpdateStepState.Pending;
    public bool IsRunning => State == UpdateStepState.Running;
    public bool IsDone => State == UpdateStepState.Done;
    public bool IsNote => State == UpdateStepState.Note;
    public bool IsProblem => State == UpdateStepState.Problem;
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
    public bool HasDiagnostic => (IsProblem || IsNote) && !string.IsNullOrEmpty(Hint);
    public TextWrapping DetailWrapping => IsRunning || IsPending ? TextWrapping.NoWrap : TextWrapping.Wrap;
}
