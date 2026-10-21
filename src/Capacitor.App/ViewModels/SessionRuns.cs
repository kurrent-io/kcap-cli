using System.Globalization;
using Avalonia.Collections;
using Capacitor.Cli.Core;

namespace Capacitor.App.ViewModels;

/// The subagents and background commands of one session, folded from each projected line's signals and tool results.
/// Owned by the workspace and shared by the chat tab and the work-context pane; every call is
/// made on the UI thread. The rows rebuild from the log on a feed reset, so nothing here is
/// persisted.
public sealed class SessionRuns(TimeProvider time) {
    readonly AvaloniaList<RunRow> _rows = new();
    readonly AvaloniaList<RunRow> _running = new();
    readonly Dictionary<string, RunRow> _byCall = new(StringComparer.Ordinal);
    /// Calls that may still go to the background, by call id; a Detached promotes one to a row,
    /// its result drops it. The session's end leaves them: a remote lane can turn it back off.
    readonly Dictionary<string, RunSignal.Started> _pending = new(StringComparer.Ordinal);
    /// The row an agent id currently belongs to; the latest Detached wins.
    readonly Dictionary<string, RunRow> _byAgent = new(StringComparer.Ordinal);
    /// Ids that arrived before the row they name: by call when the source knew it, otherwise in arrival
    /// order. The local meta file and the server's start record can both beat the call's own line.
    readonly Dictionary<string, string> _earlyByCall = new(StringComparer.Ordinal);
    readonly List<string> _earlyUnpaired = [];
    /// Within one projection, the unnamed running rows as they stood when an id was first held.
    List<RunRow>? _candidates;
    const int MaxEarly = 16;
    /// Rows per presented state, indexed by the state's value.
    readonly int[] _counts = new int[Enum.GetValues<RunState>().Length];
    int _runningBackground;
    bool _sessionOver;
    bool _opensSubagents = true;

    public IAvaloniaReadOnlyList<RunRow> Rows => _rows;
    /// The rows of Rows that present as running, in arrival order.
    public IAvaloniaReadOnlyList<RunRow> Running => _running;
    public int RunningCount => Count(RunState.Running);
    public int RunningCommandCount => _running.Count(row => row.IsShell);

    public int Count(RunState state) => _counts[(int)state];

    /// Raised after any call that changed how many rows present any one state, or how many running
    /// rows are in the background.
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

    /// Whether this lane can open a subagent's own transcript; a row offers to open only when it can.
    public bool OpensSubagents {
        get => _opensSubagents;
        set {
            if (_opensSubagents == value) return;
            _opensSubagents = value;
            foreach (var row in _rows) row.Openable = value;
        }
    }

    public void Apply(ChatProjectionResult projection) => ApplyAll([projection]);

    /// One refresh per batch: a replayed command that started and finished inside it must never
    /// surface as running, or every surface counting commands flickers when a session opens.
    public void ApplyAll(IEnumerable<ChatProjectionResult> projections) {
        foreach (var projection in projections) Fold(projection);
        Refresh();
    }

    void Fold(ChatProjectionResult projection) {
        HashSet<string>? detachedHere = null;
        _candidates = _earlyUnpaired.Count == 0 ? null : UnnamedRunning().ToList();
        foreach (var signal in projection.Runs) {
            switch (signal) {
                case RunSignal.Started started:
                    Start(started);
                    break;
                case RunSignal.Detached detached:
                    (detachedHere ??= new(StringComparer.Ordinal)).Add(detached.CallId);
                    Detach(detached);
                    break;
                case RunSignal.Finished finished:
                    Finish(finished);
                    DropHeldIfACandidateEnded();
                    break;
                case RunSignal.Identified identified:
                    Identify(identified);
                    break;
            }
        }
        foreach (var envelope in projection.Envelopes) {
            if (envelope.Kind != AcpEventKind.ToolResult || envelope.ToolCallId is not { } callId) continue;
            // The launch acknowledgement arrives beside its Detached and must not end the row; a
            // later result for the same call, an error included, does.
            if (detachedHere?.Contains(callId) == true) continue;
            _pending.Remove(callId);
            if (_byCall.TryGetValue(callId, out var row))
                row.End(envelope.ToolIsError ? RunState.Failed : RunState.Done, Stamp(envelope.TimestampIso));
            DropHeldIfACandidateEnded();
        }
        _candidates = null;
    }

    public void Clear() {
        _rows.Clear();
        _byCall.Clear();
        _byAgent.Clear();
        _pending.Clear();
        _earlyByCall.Clear();
        _earlyUnpaired.Clear();
        Refresh();
    }

    /// Re-reads the clock for the running rows' elapsed time.
    public void Tick() => Refresh();

    void Start(RunSignal.Started started) {
        if (_byCall.ContainsKey(started.CallId)) return;
        if (started.Provisional) {
            _pending[started.CallId] = started;
            return;
        }
        Add(started);
    }

    RunRow Add(RunSignal.Started started) {
        var row = new RunRow(started.CallId, started.Name, started.Description, started.At, started.Kind) { Openable = _opensSubagents };
        _byCall[started.CallId] = row;
        _rows.Add(row);
        if (row.Kind == RunKind.Agent) {
            if (_earlyByCall.Remove(started.CallId, out var agentId)) Name(row, agentId);
            else PairUnpaired();
        }
        return row;
    }

    void Detach(RunSignal.Detached detached) {
        if (!_byCall.TryGetValue(detached.CallId, out var row)) {
            if (!_pending.Remove(detached.CallId, out var pending)) return;
            row = Add(pending);
        }
        if (row.IsEnded) return;
        row.MarkBackground();
        Name(row, detached.AgentId);
        PairUnpaired();
    }

    /// Names the subagent a call started, from a source that reads it outside the projected lines.
    public void Identify(string callId, string agentId) => Identify(new RunSignal.Identified(callId, agentId));

    /// An id with no call names a subagent only when one running subagent is still unnamed: parallel calls
    /// and their start records arrive in no fixed order, so with two candidates either could be the one.
    void Identify(RunSignal.Identified identified) {
        if (identified.CallId is { } callId) {
            if (_byCall.TryGetValue(callId, out var row)) {
                if (row.Kind == RunKind.Agent) Name(row, identified.AgentId);
                PairUnpaired();
            } else if (_earlyByCall.Count < MaxEarly) {
                _earlyByCall[callId] = identified.AgentId;
            }
            return;
        }
        // An agent already placed by its own call stays there: a guess never moves it.
        if (_byAgent.ContainsKey(identified.AgentId) || _rows.Any(r => r.AgentId == identified.AgentId)) return;
        if (!_earlyUnpaired.Contains(identified.AgentId)) {
            if (_earlyUnpaired.Count == 0) _candidates = UnnamedRunning().ToList();
            if (_earlyUnpaired.Count == MaxEarly) _earlyUnpaired.RemoveAt(0);
            _earlyUnpaired.Add(identified.AgentId);
        }
        PairUnpaired();
    }

    /// One nameless id meets one running unnamed subagent: with more of either, the order says nothing.
    void PairUnpaired() {
        if (_earlyUnpaired.Count != 1) return;
        var unnamed = UnnamedRunning().Take(2).ToList();
        if (unnamed.Count == 1) Name(unnamed[0], _earlyUnpaired[0]);
    }

    // A candidate that ends could have been the held ids' own, so they can no longer be placed on whoever is left —
    // dropped at once, before a later signal in the same projection pairs them.
    void DropHeldIfACandidateEnded() {
        if (_candidates?.Any(row => row.IsEnded) == true) _earlyUnpaired.Clear();
    }

    IEnumerable<RunRow> UnnamedRunning() =>
        _rows.Where(r => r is { Kind: RunKind.Agent, AgentId: null, IsEnded: false, IsBackground: false });

    /// The call's own id outranks an earlier guess, and the agent's end then finds this row.
    void Name(RunRow row, string agentId) {
        if (row.AgentId is { } previous && previous != agentId
            && _byAgent.TryGetValue(previous, out var held) && ReferenceEquals(held, row))
            _byAgent.Remove(previous);
        row.Identify(agentId);
        _byAgent[agentId] = row;
        _earlyUnpaired.Remove(agentId);
    }

    /// A known call id decides alone and never falls through to the agent id: a repeated
    /// notification for a first execution must not end a second one holding the same id.
    void Finish(RunSignal.Finished finished) {
        var outcome = finished.Outcome switch {
            null                    => (RunState?)null,
            RunOutcome.Failed  => RunState.Failed,
            RunOutcome.Stopped => RunState.Stopped,
            _                       => RunState.Done,
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
        StableRows.Sync(_running, _rows.Where(row => row.IsRunning));
        var runningBackground = _running.Count(row => row.IsBackground);
        if (counts.SequenceEqual(_counts) && runningBackground == _runningBackground) return;
        counts.CopyTo(_counts);
        _runningBackground = runningBackground;
        Changed?.Invoke();
    }
}
