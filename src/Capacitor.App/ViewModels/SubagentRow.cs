using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// One subagent as the sidebar shows it. Outcome is the row's own state; State is what it
/// presents, which reads Stopped for a still-running row once the session is over.
public sealed class SubagentRow : ReactiveObject {
    bool _isBackground;
    SubagentState _state = SubagentState.Running;
    string _stateText = "";

    public SubagentRow(string callId, string name, string description, DateTimeOffset startedAt) {
        CallId = callId;
        Name = name;
        Description = description;
        StartedAt = startedAt;
    }

    public string CallId { get; }
    public string Name { get; }
    public string Description { get; }
    public DateTimeOffset StartedAt { get; }
    public DateTimeOffset? EndedAt { get; private set; }
    public SubagentState Outcome { get; private set; } = SubagentState.Running;
    public bool IsEnded => Outcome != SubagentState.Running;

    public bool IsBackground { get => _isBackground; private set => this.RaiseAndSetIfChanged(ref _isBackground, value); }

    public SubagentState State {
        get => _state;
        private set {
            if (_state == value) return;
            this.RaiseAndSetIfChanged(ref _state, value);
            this.RaisePropertyChanged(nameof(IsRunning));
            this.RaisePropertyChanged(nameof(IsDone));
            this.RaisePropertyChanged(nameof(IsFailed));
            this.RaisePropertyChanged(nameof(IsStopped));
        }
    }

    public string StateText { get => _stateText; private set => this.RaiseAndSetIfChanged(ref _stateText, value); }

    public bool IsRunning => State == SubagentState.Running;
    public bool IsDone    => State == SubagentState.Done;
    public bool IsFailed  => State == SubagentState.Failed;
    public bool IsStopped => State == SubagentState.Stopped;

    internal void MarkBackground() => IsBackground = true;

    internal void End(SubagentState outcome, DateTimeOffset at) {
        Outcome = outcome;
        EndedAt = at;
    }

    internal void Present(bool sessionOver, DateTimeOffset now) {
        var stoppedBySession = sessionOver && !IsEnded;
        State = stoppedBySession ? SubagentState.Stopped : Outcome;
        // Nothing dates an end the session imposed, so that row shows no duration.
        StateText = stoppedBySession ? "stopped" : Text(State, (EndedAt ?? now) - StartedAt);
    }

    static string Text(SubagentState state, TimeSpan elapsed) => state switch {
        SubagentState.Running => $"running · {Duration(elapsed)}",
        SubagentState.Failed  => $"failed · {Duration(elapsed)}",
        SubagentState.Stopped => $"stopped · {Duration(elapsed)}",
        _                     => Duration(elapsed),
    };

    internal static string Duration(TimeSpan elapsed) {
        var seconds = Math.Max(0, (long)elapsed.TotalSeconds);
        return seconds < 60 ? $"{seconds}s" : $"{seconds / 60}m {seconds % 60:00}s";
    }
}
