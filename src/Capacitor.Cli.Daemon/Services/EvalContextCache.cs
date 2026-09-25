using System.Collections.Concurrent;
using Capacitor.Cli.Core.Eval;
using Capacitor.Cli.Core.Eval.Evidence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Per-evalRunId cache of a prepared run: a legacy <see cref="EvalService.EvalContext"/> or an evidence-route
/// <see cref="EvidenceRunSetup"/>. Entries slide on access and expire when idle; an entry leaving the cache for any reason is
/// disposed, so an evidence run's private directory never outlives it.</summary>
internal sealed class EvalContextCache : IDisposable, IAsyncDisposable {
    sealed record Entry(object Context, DateTimeOffset LastAccessed, IDisposable? Owned);

    // Idle, not absolute: every per-question read refreshes the entry, so only an abandoned run expires.
    static readonly TimeSpan MaxIdle       = TimeSpan.FromMinutes(30);
    static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    readonly ConcurrentDictionary<string, Entry> _entries = new();
    readonly ITimer       _sweepTimer;
    readonly TimeProvider _time;
    readonly ILogger      _logger;

    public EvalContextCache(TimeProvider time, ILogger<EvalContextCache>? logger = null) {
        _time       = time;
        _logger     = logger ?? (ILogger)NullLogger.Instance;
        _sweepTimer = time.CreateTimer(_ => Sweep(), null, SweepInterval, SweepInterval);
    }

    public void Put(string evalRunId, EvalService.EvalContext ctx) => PutEntry(evalRunId, new(ctx, _time.GetUtcNow(), null));

    /// <summary>Caches a prepared evidence run; <paramref name="owned"/> (the client its scope and readers send through) is
    /// disposed with it.</summary>
    public void Put(string evalRunId, EvidenceRunSetup setup, IDisposable? owned = null) => PutEntry(evalRunId, new(setup, _time.GetUtcNow(), owned));

    public EvalService.EvalContext? Get(string evalRunId) => Touch(evalRunId) as EvalService.EvalContext;

    public EvidenceRunSetup? GetEvidence(string evalRunId) => Touch(evalRunId) as EvidenceRunSetup;

    public void Remove(string evalRunId) {
        if (_entries.TryRemove(evalRunId, out var entry)) Release(evalRunId, entry);
    }

    public int Count => _entries.Count;

    public void Dispose() {
        _sweepTimer.Dispose();
        foreach (var key in _entries.Keys) Remove(key);
    }

    public ValueTask DisposeAsync() {
        Dispose();
        return ValueTask.CompletedTask;
    }

    void PutEntry(string evalRunId, Entry entry) {
        while (true) {
            if (_entries.TryAdd(evalRunId, entry)) return;
            if (_entries.TryGetValue(evalRunId, out var replaced) && _entries.TryUpdate(evalRunId, entry, replaced)) {
                if (!ReferenceEquals(replaced.Context, entry.Context)) Release(evalRunId, replaced);
                return;
            }
        }
    }

    object? Touch(string evalRunId) {
        if (!_entries.TryGetValue(evalRunId, out var entry)) return null;
        var now = _time.GetUtcNow();
        if (now - entry.LastAccessed > MaxIdle) {
            if (_entries.TryRemove(new KeyValuePair<string, Entry>(evalRunId, entry))) Release(evalRunId, entry);
            return null;
        }
        // A concurrent read or removal only loses this refresh.
        _entries.TryUpdate(evalRunId, entry with { LastAccessed = now }, entry);
        return entry.Context;
    }

    void Sweep() {
        var now = _time.GetUtcNow();
        foreach (var kvp in _entries)
            if (now - kvp.Value.LastAccessed > MaxIdle && _entries.TryRemove(kvp)) Release(kvp.Key, kvp.Value);
    }

    // Disposal is synchronous underneath, so the sync paths wait on it; one failure is logged and never stops the rest.
    void Release(string evalRunId, Entry entry) {
        if (entry.Context is EvidenceRunSetup setup) {
            try {
                var disposal = setup.DisposeAsync();
                if (!disposal.IsCompletedSuccessfully) disposal.AsTask().GetAwaiter().GetResult();
            } catch (Exception e) {
                _logger.LogWarning(e, "Could not remove the run directory of eval {RunId}", evalRunId);
            }
        }
        entry.Owned?.Dispose();
    }
}
