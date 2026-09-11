using System.Collections.Frozen;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace Capacitor.App.Services;

/// Per-session sets of unresolved server request ids for sessions the app has not opened. A
/// pending ping names no request, so it marks the session dirty and a headless reconciliation
/// of the session's stream fills the set; the dirty mark outlives disconnects and fetch failures
/// until a reconciliation completes. A response removes one id; one the set never held means
/// the set is out of date, so it re-reconciles rather than trusting the count.
public sealed class SessionAttentionTracker : IDisposable {
    static readonly TimeSpan[] Retry = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    sealed class Session {
        public readonly HashSet<string> Ids = new(StringComparer.Ordinal);
        public bool Dirty;
        public int Failures;
        public int Attempt;
        public ITimer? Timer;
    }

    readonly SessionDetailReader _readDetail;
    readonly TimeProvider _time;
    readonly TimeSpan _debounce;
    readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    readonly BehaviorSubject<IReadOnlySet<string>> _attention = new(FrozenSet<string>.Empty);
    readonly IDisposable _subscriptions;
    readonly CancellationTokenSource _lifetime = new();
    readonly Lock _lock = new();
    bool _connected;
    bool _disposed;

    public SessionAttentionTracker(IServerLane lane, SessionDetailReader readDetail, TimeProvider time, TimeSpan? debounce = null) {
        _readDetail = readDetail;
        _time = time;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(300);
        var status = lane.Status.Select(s => s.State == ServerLaneState.Connected).DistinctUntilChanged().Subscribe(OnLane);
        var pending = lane.PermissionPending.Subscribe(OnPending);
        var responded = lane.PermissionResponded.Subscribe(OnResponded);
        _subscriptions = new CompositeDisposable(status, pending, responded);
    }

    public IObservable<IReadOnlySet<string>> SessionsWithAttention => _attention.AsObservable();

    void OnPending(string sessionId) {
        lock (_lock) {
            if (_disposed) return;
            var s = Get(sessionId);
            s.Dirty = true;
            Schedule(sessionId, s, _debounce);
        }
    }

    void OnResponded(PermissionRespondedPing ping) {
        lock (_lock) {
            if (_disposed) return;
            if (ping.RequestId is null) {
                if (!_sessions.TryGetValue(ping.SessionId, out var known)) return;
                known.Ids.Clear();
                Supersede(ping.SessionId, known);
                Prune(ping.SessionId, known);
                Publish();
                return;
            }
            var s = Get(ping.SessionId);
            if (s.Ids.Remove(ping.RequestId)) {
                Supersede(ping.SessionId, s);
                Prune(ping.SessionId, s);
                Publish();
                return;
            }
            s.Dirty = true;
            Schedule(ping.SessionId, s, _debounce);
        }
    }

    // Caller holds _lock. A reconciliation already in flight took its snapshot before this
    // response, so applying it would put the settled ids straight back; the session is still owed
    // a reconciliation if it was marked dirty, and re-arming is what keeps that owed one alive.
    void Supersede(string sessionId, Session s) {
        s.Attempt++;
        if (s.Dirty) Schedule(sessionId, s, _debounce);
    }

    // Caller holds _lock. Nothing waiting and nothing owed, same terms ReconcileAsync prunes on.
    void Prune(string sessionId, Session s) {
        if (s.Ids.Count > 0 || s.Dirty) return;
        s.Timer?.Dispose();
        s.Timer = null;
        _sessions.Remove(sessionId);
    }

    void OnLane(bool connected) {
        lock (_lock) {
            if (_disposed) return;
            _connected = connected;
            // A zero delay reconciles on this thread and can drop the session it just emptied,
            // so walk a snapshot rather than the dictionary being mutated underneath.
            foreach (var (sid, s) in _sessions.ToArray()) {
                s.Failures = 0;
                if (!connected) { s.Attempt++; s.Timer?.Dispose(); s.Timer = null; continue; }
                if (s.Dirty || s.Ids.Count > 0) Schedule(sid, s, TimeSpan.Zero);
            }
        }
    }

    // Caller holds _lock.
    Session Get(string sessionId) {
        if (!_sessions.TryGetValue(sessionId, out var s)) { s = new Session(); _sessions[sessionId] = s; }
        return s;
    }

    // Caller holds _lock. Guards against a session pruned and re-created under the same id while
    // work for the older one was still in flight.
    bool IsCurrent(string sessionId, Session s) => _sessions.TryGetValue(sessionId, out var current) && ReferenceEquals(current, s);

    // Caller holds _lock. A schedule supersedes any earlier one for the session.
    void Schedule(string sessionId, Session s, TimeSpan delay) {
        s.Timer?.Dispose();
        s.Timer = null;
        if (_disposed || !_connected) return;
        var attempt = ++s.Attempt;
        var timer = _time.CreateTimer(_ => Fire(sessionId, s, attempt), null, delay, Timeout.InfiniteTimeSpan);
        // A zero delay runs the callback inside CreateTimer, so whatever it scheduled in turn is
        // already the session's timer by the time this spent handle comes back: keep that one.
        if (s.Timer is null && s.Attempt == attempt && IsCurrent(sessionId, s)) s.Timer = timer;
        else timer.Dispose();
    }

    void Fire(string sessionId, Session s, int attempt) {
        lock (_lock) {
            if (_disposed || s.Attempt != attempt || !IsCurrent(sessionId, s)) return;
            _ = ReconcileAsync(sessionId, s, attempt);
        }
    }

    async Task ReconcileAsync(string sessionId, Session s, int attempt) {
        SessionDetailFetch fetch;
        try { fetch = await _readDetail(sessionId, _lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        catch (Exception) { fetch = new(null); }

        lock (_lock) {
            if (_disposed || s.Attempt != attempt || !IsCurrent(sessionId, s)) return;
            if (fetch.Detail is null && !fetch.NotFound) {
                if (!_connected) return;
                Schedule(sessionId, s, Retry[Math.Min(s.Failures++, Retry.Length - 1)]);
                return;
            }
            s.Ids.Clear();
            if (fetch.Detail is { } detail && InterruptReconciliation.FromDetail(detail) is { Ended: false } reconciled)
                foreach (var p in reconciled.Pending) s.Ids.Add(p.RequestId);
            s.Dirty = false;
            s.Failures = 0;
            if (s.Ids.Count == 0) { s.Timer?.Dispose(); s.Timer = null; _sessions.Remove(sessionId); }
            Publish();
        }
    }

    // Caller holds _lock.
    void Publish() {
        var next = _sessions.Where(kv => kv.Value.Ids.Count > 0).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        if (next.SetEquals(_attention.Value)) return;
        _attention.OnNext(next.Count == 0 ? FrozenSet<string>.Empty : next);
    }

    public void Dispose() {
        lock (_lock) {
            if (_disposed) return;
            _disposed = true;
            foreach (var s in _sessions.Values) { s.Attempt++; s.Timer?.Dispose(); s.Timer = null; }
            _sessions.Clear();
        }
        _subscriptions.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
        _attention.OnCompleted();
        _attention.Dispose();
    }
}
