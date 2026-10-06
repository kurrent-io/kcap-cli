using Capacitor.Cli.Core;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// One subagent or background command as the sidebar shows it. Outcome is the row's own state;
/// State is what it presents, which reads Stopped for a still-running row once the session is over.
public sealed class RunRow : ReactiveObject {
    bool _isBackground;
    bool _outcomeUnknown;
    RunState _state = RunState.Running;
    string _stateText = "";

    public RunRow(string callId, string name, string description, DateTimeOffset startedAt, RunKind kind = RunKind.Agent) {
        CallId = callId;
        Name = name;
        Description = description;
        StartedAt = startedAt;
        Kind = kind;
    }

    public string CallId { get; }
    public string Name { get; }
    public string Description { get; }
    public DateTimeOffset StartedAt { get; }
    public RunKind Kind { get; }
    public bool IsShell => Kind == RunKind.Shell;
    public DateTimeOffset? EndedAt { get; private set; }
    public RunState Outcome { get; private set; } = RunState.Running;
    public bool IsEnded => Outcome != RunState.Running;

    public bool IsBackground { get => _isBackground; private set => this.RaiseAndSetIfChanged(ref _isBackground, value); }

    public RunState State {
        get => _state;
        private set {
            if (_state == value) return;
            this.RaiseAndSetIfChanged(ref _state, value);
            this.RaisePropertyChanged(nameof(IsRunning));
            this.RaisePropertyChanged(nameof(IsFailed));
        }
    }

    public string StateText { get => _stateText; private set => this.RaiseAndSetIfChanged(ref _stateText, value); }

    public bool IsRunning => State == RunState.Running;
    public bool IsFailed  => State == RunState.Failed;

    internal void MarkBackground() => IsBackground = true;

    /// An end with an outcome is final. One without reads as done until one with an outcome
    /// arrives, and the earlier of the two dates the row: the server stamps a stop when it heard
    /// it, which an import or a spooled hook puts long after the run ended.
    internal void End(RunState? outcome, DateTimeOffset at) {
        if (IsEnded && (!_outcomeUnknown || outcome is null)) return;
        EndedAt = EndedAt is { } bare && bare < at ? bare : at;
        Outcome = outcome ?? RunState.Done;
        _outcomeUnknown = outcome is null;
    }

    internal void Present(bool sessionOver, DateTimeOffset now) {
        var stoppedBySession = sessionOver && !IsEnded;
        State = stoppedBySession ? RunState.Stopped : Outcome;
        // Nothing dates an end the session imposed, so that row shows no duration.
        StateText = stoppedBySession ? "stopped" : Text(State, IsBackground, (EndedAt ?? now) - StartedAt);
    }

    // Background only matters while the run is live: it says the chat is not waiting on it.
    static string Text(RunState state, bool background, TimeSpan elapsed) => state switch {
        RunState.Running when background => $"running in background · {Duration(elapsed)}",
        RunState.Running => $"running · {Duration(elapsed)}",
        RunState.Failed  => $"failed · {Duration(elapsed)}",
        RunState.Stopped => $"stopped · {Duration(elapsed)}",
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
