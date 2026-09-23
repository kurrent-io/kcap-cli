using System.Globalization;
using Avalonia.Collections;
using Capacitor.Cli.Core;

namespace Capacitor.App.ViewModels;

/// The subagents of one session, folded from each projected line's signals and tool results.
/// Owned by the workspace and shared by the chat tab and the work-context pane; every call is
/// made on the UI thread. The rows rebuild from the log on a feed reset, so nothing here is
/// persisted.
public sealed class SessionSubagents(TimeProvider time) {
    readonly AvaloniaList<SubagentRow> _rows = new();
    readonly Dictionary<string, SubagentRow> _byCall = new(StringComparer.Ordinal);
    /// The row an agent id currently belongs to; the latest Detached wins.
    readonly Dictionary<string, SubagentRow> _byAgent = new(StringComparer.Ordinal);
    /// Rows per presented state, indexed by the state's value.
    readonly int[] _counts = new int[Enum.GetValues<SubagentState>().Length];
    bool _sessionOver;

    public IAvaloniaReadOnlyList<SubagentRow> Rows => _rows;
    public int RunningCount => Count(SubagentState.Running);

    public int Count(SubagentState state) => _counts[(int)state];

    /// Raised after any call that changed how many rows present any one state.
    public event Action? Changed;

    /// The lane's verdict that nothing more will arrive: a view over the rows, not a transition,
    /// so a remote row coming back turns it off and its running rows read as running again.
    public bool SessionOver {
        get => _sessionOver;
        set {
            if (_sessionOver == value) return;
            _sessionOver = value;
            Refresh();
        }
    }

    public void Apply(ChatProjectionResult projection) {
        HashSet<string>? detachedHere = null;
        foreach (var signal in projection.Subagents) {
            switch (signal) {
                case SubagentSignal.Started started:
                    Start(started);
                    break;
                case SubagentSignal.Detached detached:
                    (detachedHere ??= new(StringComparer.Ordinal)).Add(detached.CallId);
                    Detach(detached);
                    break;
                case SubagentSignal.Finished finished:
                    Finish(finished);
                    break;
            }
        }
        foreach (var envelope in projection.Envelopes) {
            if (envelope.Kind != AcpEventKind.ToolResult || envelope.ToolCallId is not { } callId) continue;
            // The launch acknowledgement arrives beside its Detached and must not end the row; a
            // later result for the same call, an error included, does.
            if (detachedHere?.Contains(callId) == true) continue;
            if (_byCall.TryGetValue(callId, out var row))
                row.End(envelope.ToolIsError ? SubagentState.Failed : SubagentState.Done, Stamp(envelope.TimestampIso));
        }
        Refresh();
    }

    public void Clear() {
        _rows.Clear();
        _byCall.Clear();
        _byAgent.Clear();
        Refresh();
    }

    /// Re-reads the clock for the running rows' elapsed time.
    public void Tick() => Refresh();

    void Start(SubagentSignal.Started started) {
        if (_byCall.ContainsKey(started.CallId)) return;
        var row = new SubagentRow(started.CallId, started.Name, started.Description, started.At);
        _byCall[started.CallId] = row;
        _rows.Add(row);
    }

    void Detach(SubagentSignal.Detached detached) {
        if (!_byCall.TryGetValue(detached.CallId, out var row) || row.IsEnded) return;
        row.MarkBackground();
        _byAgent[detached.AgentId] = row;
    }

    /// A known call id decides alone and never falls through to the agent id: a repeated
    /// notification for a first execution must not end a second one holding the same id.
    void Finish(SubagentSignal.Finished finished) {
        var outcome = finished.Outcome switch {
            null                    => (SubagentState?)null,
            SubagentOutcome.Failed  => SubagentState.Failed,
            SubagentOutcome.Stopped => SubagentState.Stopped,
            _                       => SubagentState.Done,
        };
        if (finished.CallId is { } callId && _byCall.TryGetValue(callId, out var byCall)) {
            byCall.End(outcome, finished.At);
            return;
        }
        // A completion dated before the row started belongs to an earlier execution.
        if (finished.AgentId is { } agentId && _byAgent.TryGetValue(agentId, out var byAgent)
            && finished.At >= byAgent.StartedAt)
            byAgent.End(outcome, finished.At);
    }

    DateTimeOffset Stamp(string? iso) =>
        iso is not null && DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? at : time.GetUtcNow();

    void Refresh() {
        var now = time.GetUtcNow();
        Span<int> counts = stackalloc int[_counts.Length];
        foreach (var row in _rows) {
            row.Present(_sessionOver, now);
            counts[(int)row.State]++;
        }
        if (counts.SequenceEqual(_counts)) return;
        counts.CopyTo(_counts);
        Changed?.Invoke();
    }
}
