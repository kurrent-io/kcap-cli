namespace Capacitor.Cli.Core.Harness.Grok;

/// <summary>The parent watcher's view of its Grok subagents across ticks: which are waiting to be
/// registered, and which have finished and are waiting to be stopped.</summary>
public sealed class GrokSubagentTracker {
    readonly Dictionary<string, string?> _types    = new(StringComparer.Ordinal);
    readonly HashSet<string>             _started  = new(StringComparer.Ordinal);
    readonly HashSet<string>             _finished = new(StringComparer.Ordinal);
    readonly HashSet<string>             _stopped  = new(StringComparer.Ordinal);

    /// <summary>Whether the whole parent transcript has been read. A watcher resumes past the server's
    /// cursor, so without the seed a restart never sees the children spawned before it.</summary>
    public bool Seeded { get; private set; }

    public void Seed(IEnumerable<GrokSubagentEvent> events) {
        Observe(events);
        Seeded = true;
    }

    public void Observe(IEnumerable<GrokSubagentEvent> events) {
        foreach (var e in events) {
            if (!_types.TryGetValue(e.ChildSessionId, out var known) || known is null) _types[e.ChildSessionId] = e.SubagentType;
            if (e.Finished) _finished.Add(e.ChildSessionId);
        }
    }

    public IReadOnlyList<(string Child, string? Type)> AwaitingStart() =>
        [.. _types.Where(t => !_started.Contains(t.Key) && !_stopped.Contains(t.Key)).Select(t => (t.Key, t.Value))];

    public IReadOnlyList<(string Child, string? Type)> AwaitingStop() =>
        [.. _types.Where(t => _started.Contains(t.Key) && _finished.Contains(t.Key) && !_stopped.Contains(t.Key)).Select(t => (t.Key, t.Value))];

    /// <summary>Registered children still running, whose watchers must stay up.</summary>
    public IReadOnlyList<(string Child, string? Type)> Active() =>
        [.. _types.Where(t => _started.Contains(t.Key) && !_finished.Contains(t.Key) && !_stopped.Contains(t.Key)).Select(t => (t.Key, t.Value))];

    public void Started(string child) => _started.Add(child);

    public void Stopped(string child) => _stopped.Add(child);
}
