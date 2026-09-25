using System.Collections.Concurrent;
using Capacitor.Cli.Core.Eval;
using Capacitor.Cli.Core.Eval.Evidence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Per-evalRunId cache of a prepared run: a legacy <see cref="EvalService.EvalContext"/> or an evidence-route
/// <see cref="EvidenceRunSetup"/>. Entries slide on access and expire when idle; an entry leaving the cache for any reason is
/// disposed, so an evidence run's private directory never outlives it. A phase works on an evidence run only through a
/// lease: leaving the cache cancels the lease, and disposal waits until the last lease ends.</summary>
internal sealed class EvalContextCache : IDisposable, IAsyncDisposable {
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

    public void Put(string evalRunId, EvalService.EvalContext ctx) => PutEntry(evalRunId, new(evalRunId, ctx, null, _time.GetUtcNow(), _logger));

    /// <summary>Caches a prepared evidence run; <paramref name="owned"/> (the client its scope and readers send through) is
    /// disposed with it.</summary>
    public void Put(string evalRunId, EvidenceRunSetup setup, IDisposable? owned = null) => PutEntry(evalRunId, new(evalRunId, setup, owned, _time.GetUtcNow(), _logger));

    public EvalService.EvalContext? Get(string evalRunId) {
        var now = _time.GetUtcNow();
        return Live(evalRunId, now) is { } entry && entry.TryTouch(now, lease: false) ? entry.Context as EvalService.EvalContext : null;
    }

    /// <summary>The cached evidence run, held until the lease is disposed; null when there is none or it has left the cache.</summary>
    public EvidenceRunLease? LeaseEvidence(string evalRunId) {
        var now = _time.GetUtcNow();
        if (Live(evalRunId, now) is not { Context: EvidenceRunSetup setup } entry || !entry.TryTouch(now, lease: true)) return null;
        return new EvidenceRunLease(setup, entry.Cancelled, () => entry.EndLease(_time.GetUtcNow()));
    }

    public void Remove(string evalRunId) {
        if (_entries.TryRemove(evalRunId, out var entry)) entry.Retire(idleAsOf: null);
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
            if (!_entries.TryGetValue(evalRunId, out var replaced)) continue;
            // The same context put again only refreshes the entry already holding it.
            if (ReferenceEquals(replaced.Context, entry.Context) && replaced.TryTouch(_time.GetUtcNow(), lease: false)) return;
            if (_entries.TryUpdate(evalRunId, entry, replaced)) {
                replaced.Retire(idleAsOf: null);
                return;
            }
        }
    }

    // The entry, unless it has gone idle, in which case it is retired here.
    Entry? Live(string evalRunId, DateTimeOffset now) {
        if (!_entries.TryGetValue(evalRunId, out var entry)) return null;
        if (!entry.Retire(idleAsOf: now)) return entry;
        _entries.TryRemove(new KeyValuePair<string, Entry>(evalRunId, entry));
        return null;
    }

    void Sweep() {
        var now = _time.GetUtcNow();
        foreach (var kvp in _entries)
            if (kvp.Value.Retire(idleAsOf: now)) _entries.TryRemove(kvp);
    }

    // The token source is disposed by whichever of the cancel and the last release finishes second, so a lease ending on
    // another thread can never dispose it mid-cancel.
    sealed class Entry(string evalRunId, object context, IDisposable? owned, DateTimeOffset now, ILogger logger) : IDisposable {
        readonly Lock _lock = new();
        readonly CancellationTokenSource _retired = new();
        DateTimeOffset _lastAccessed = now;
        int  _leases;
        bool _isRetired;
        bool _cancelled;
        bool _released;

        public object            Context   => context;
        public CancellationToken Cancelled => _retired.Token;

        public bool TryTouch(DateTimeOffset at, bool lease) {
            lock (_lock) {
                if (_isRetired) return false;
                _lastAccessed = at;
                if (lease) _leases++;
                return true;
            }
        }

        public void EndLease(DateTimeOffset at) {
            bool release, dispose;
            lock (_lock) {
                _leases--;
                _lastAccessed = at;
                release = ShouldRelease();
                dispose = release && _cancelled;
            }
            if (release) Release();
            if (dispose) Dispose();
        }

        /// <summary>Takes the entry out of service: with <paramref name="idleAsOf"/>, only when it is unleased and has been idle
        /// past the limit at that time. True when the entry is retired, now or already.</summary>
        public bool Retire(DateTimeOffset? idleAsOf) {
            bool release;
            lock (_lock) {
                if (_isRetired) return true;
                if (idleAsOf is { } at && (_leases > 0 || at - _lastAccessed <= MaxIdle)) return false;
                _isRetired = true;
                release = ShouldRelease();
            }
            try { _retired.Cancel(); }
            catch (AggregateException e) { logger.LogWarning(e, "A phase of eval {RunId} failed to observe its cancellation", evalRunId); }
            if (release) Release();
            bool dispose;
            lock (_lock) {
                _cancelled = true;
                dispose    = _released;
            }
            if (dispose) Dispose();
            return true;
        }

        bool ShouldRelease() {
            if (!_isRetired || _leases > 0 || _released) return false;
            _released = true;
            return true;
        }

        // Disposal is synchronous underneath, so it is waited on here; a failure is logged and never stops the rest.
        void Release() {
            if (context is EvidenceRunSetup setup) {
                try {
                    var disposal = setup.DisposeAsync();
                    if (!disposal.IsCompletedSuccessfully) disposal.AsTask().GetAwaiter().GetResult();
                } catch (Exception e) {
                    logger.LogWarning(e, "Could not remove the run directory of eval {RunId}", evalRunId);
                }
            }
            owned?.Dispose();
        }

        public void Dispose() => _retired.Dispose();
    }
}
