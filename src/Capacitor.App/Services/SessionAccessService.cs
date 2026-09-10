using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace Capacitor.App.Services;

/// Owns every per-session hub subscription the app holds: the access watch (the server's own
/// revocation signal) and the chat group (where permission payloads arrive). One attempt at a
/// time per session, numbered so a superseded attempt's result is dropped; every attempt is
/// re-run on a reconnect and on the server's access-changed ping, and a transient failure
/// retries on a fixed ladder while the lane stays up.
public sealed class SessionAccessService : IDisposable {
    static readonly TimeSpan[] Retry = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    sealed class Entry(string sessionId) {
        public string SessionId { get; } = sessionId;
        public BehaviorSubject<SessionAccessState> State { get; } = new(SessionAccessState.Establishing);
        public int Leases;
        public int Attempt;
        public int Failures;
        public ITimer? RetryTimer;
    }

    readonly IServerLane _lane;
    readonly TimeProvider _time;
    readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    readonly Subject<(string SessionId, SessionAccessState State)> _transitions = new();
    readonly Lock _lock = new();
    readonly IDisposable _subscriptions;
    bool _connected;
    bool _disposed;

    public SessionAccessService(IServerLane lane, TimeProvider time) {
        _lane = lane;
        _time = time;
        var status = lane.Status
            .Select(s => s.State == ServerLaneState.Connected)
            .DistinctUntilChanged()
            .Subscribe(OnLane);
        var changed = lane.SessionAccessChanged.Subscribe(sid => { lock (_lock) { if (_entries.TryGetValue(sid, out var e)) Begin(e); } });
        _subscriptions = new CompositeDisposable(status, changed);
    }

    public IObservable<(string SessionId, SessionAccessState State)> Transitions => _transitions.AsObservable();

    public SessionAccessLease Acquire(string sessionId) {
        Entry entry;
        var fresh = false;
        lock (_lock) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(sessionId, out entry!)) { entry = new Entry(sessionId); _entries[sessionId] = entry; fresh = true; }
            entry.Leases++;
            if (fresh) Begin(entry);
        }
        return new SessionAccessLease(sessionId, entry.State, Release);
    }

    void Release(SessionAccessLease lease) {
        Entry gone;
        lock (_lock) {
            if (!_entries.TryGetValue(lease.SessionId, out var entry)) return;
            if (--entry.Leases > 0) return;
            _entries.Remove(lease.SessionId);
            entry.Attempt++;
            entry.RetryTimer?.Dispose();
            gone = entry;
        }
        gone.State.OnCompleted();
        if (_connected) _ = _lane.UnsubscribeFromChatAsync(lease.SessionId, CancellationToken.None);
    }

    void OnLane(bool connected) {
        List<Entry> entries;
        lock (_lock) {
            _connected = connected;
            entries = [.. _entries.Values];
            foreach (var e in entries) {
                e.Failures = 0;
                if (connected) Begin(e);
                else { e.Attempt++; e.RetryTimer?.Dispose(); e.RetryTimer = null; Publish(e, SessionAccessState.Unavailable); }
            }
        }
    }

    // Caller holds _lock. Always publishes Establishing first, even when already establishing —
    // that's what lets a subsequent Established re-emit on this attempt despite Publish's own
    // dedup, so a consumer reconciling per establishment sees every one, reconnects included.
    void Begin(Entry entry) {
        entry.RetryTimer?.Dispose();
        entry.RetryTimer = null;
        var attempt = ++entry.Attempt;
        if (!_connected) { Publish(entry, SessionAccessState.Unavailable); return; }
        Publish(entry, SessionAccessState.Establishing);
        _ = Task.Run(() => EstablishAsync(entry, attempt));
    }

    async Task EstablishAsync(Entry entry, int attempt) {
        SessionAccessState verdict;
        try {
            var watch = await _lane.RegisterSessionAccessWatchAsync(entry.SessionId, CancellationToken.None).ConfigureAwait(false);
            if (watch.Result == HubCallResult.Ok) {
                var chat = await _lane.SubscribeToChatAsync(entry.SessionId, CancellationToken.None).ConfigureAwait(false);
                verdict = Classify(chat);
            } else {
                verdict = Classify(watch);
            }
        } catch (Exception) {
            verdict = SessionAccessState.Unavailable;
        }

        lock (_lock) {
            if (_disposed || entry.Attempt != attempt) return; // superseded by a newer attempt: drop
            Publish(entry, verdict);
            if (verdict != SessionAccessState.Unavailable || !_connected) { entry.Failures = 0; return; }
            var delay = Retry[Math.Min(entry.Failures++, Retry.Length - 1)];
            entry.RetryTimer = _time.CreateTimer(_ => { lock (_lock) { if (entry.Attempt == attempt) Begin(entry); } }, null, delay, Timeout.InfiniteTimeSpan);
        }
    }

    static SessionAccessState Classify(HubCallOutcome outcome) => outcome.Result switch {
        HubCallResult.Ok => SessionAccessState.Established,
        HubCallResult.Denied => SessionAccessState.Denied,
        _ => SessionAccessState.Unavailable,
    };

    // Caller holds _lock.
    void Publish(Entry entry, SessionAccessState state) {
        if (entry.State.Value == state && state != SessionAccessState.Establishing) return;
        entry.State.OnNext(state);
        _transitions.OnNext((entry.SessionId, state));
    }

    public void Dispose() {
        lock (_lock) {
            if (_disposed) return;
            _disposed = true;
            foreach (var e in _entries.Values) { e.Attempt++; e.RetryTimer?.Dispose(); }
        }
        _subscriptions.Dispose();
        _transitions.Dispose();
    }
}
