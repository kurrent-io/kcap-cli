using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// One subagent as the sidebar shows it. Outcome is the row's own state; State is what it
/// presents, which reads Stopped for a still-running row once the session is over.
public sealed class SubagentRow : ReactiveObject {
    bool _isBackground;
    bool _outcomeUnknown;
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
            this.RaisePropertyChanged(nameof(IsFailed));
        }
    }

    public string StateText { get => _stateText; private set => this.RaiseAndSetIfChanged(ref _stateText, value); }

    public bool IsRunning => State == SubagentState.Running;
    public bool IsFailed  => State == SubagentState.Failed;

    internal void MarkBackground() => IsBackground = true;

    /// An end with an outcome is final. One without reads as done until one with an outcome
    /// arrives, and the earlier of the two dates the row: the server stamps a stop when it heard
    /// it, which an import or a spooled hook puts long after the run ended.
    internal void End(SubagentState? outcome, DateTimeOffset at) {
        if (IsEnded && (!_outcomeUnknown || outcome is null)) return;
        EndedAt = EndedAt is { } bare && bare < at ? bare : at;
        Outcome = outcome ?? SubagentState.Done;
        _outcomeUnknown = outcome is null;
    }

    internal void Present(bool sessionOver, DateTimeOffset now) {
        var stoppedBySession = sessionOver && !IsEnded;
        State = stoppedBySession ? SubagentState.Stopped : Outcome;
        // Nothing dates an end the session imposed, so that row shows no duration.
        StateText = stoppedBySession ? "stopped" : Text(State, IsBackground, (EndedAt ?? now) - StartedAt);
    }

    // Background only matters while the run is live: it says the chat is not waiting on it.
    static string Text(SubagentState state, bool background, TimeSpan elapsed) => state switch {
        SubagentState.Running when background => $"running in background · {Duration(elapsed)}",
        SubagentState.Running => $"running · {Duration(elapsed)}",
        SubagentState.Failed  => $"failed · {Duration(elapsed)}",
        SubagentState.Stopped => $"stopped · {Duration(elapsed)}",
        _                     => Duration(elapsed),
    };

    internal static string Duration(TimeSpan elapsed) {
        var seconds = Math.Max(0, (long)elapsed.TotalSeconds);
        return seconds switch {
            < 60   => $"{seconds}s",
            < 3600 => $"{seconds / 60}m {seconds % 60:00}s",
            _      => $"{seconds / 3600}h {seconds % 3600 / 60:00}m",
        };
    }
}
