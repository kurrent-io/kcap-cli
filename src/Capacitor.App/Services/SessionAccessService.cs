using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace Capacitor.App.Services;

/// Owns every per-session hub subscription the app holds: the access watch (the server's own
/// revocation signal) and the chat group (where permission payloads arrive). One attempt at a
/// time per session, numbered so a superseded attempt's result is dropped; every attempt is
/// re-run on a reconnect and on the server's access-changed ping, and a transient failure
/// retries on a fixed ladder while the lane stays up. The chat group is shared per session
/// rather than per attempt, so giving it back is gated on ownership, not on staleness alone —
/// and every join and give-back for one session runs through that session's gate, where the
/// ownership test and the hub call it authorizes cannot be split by a newer attempt.
public sealed class SessionAccessService : IDisposable {
    static readonly TimeSpan[] Retry = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    sealed class Entry(string sessionId) {
        public string SessionId { get; } = sessionId;
        public BehaviorSubject<SessionAccessState> State { get; } = new(SessionAccessState.Establishing);
        public int Leases;
        public int Attempt;
        public int Failures;
        public ITimer? RetryTimer;
        public string? LastDiagnostic;
    }

    readonly IServerLane _lane;
    readonly TimeProvider _time;
    readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    /// Which attempt currently holds each session's chat subscription — keyed by session, because
    /// the group is shared: two entries for one session are two claims on one membership.
    readonly Dictionary<string, (Entry Entry, int Attempt)> _chatOwners = new(StringComparer.Ordinal);
    /// Serializes one session's chat-group calls. Held across a hub call, never with _lock.
    readonly SessionGates _chatGates = new();
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
        var changed = lane.SessionAccessChanged.Subscribe(sid => {
            lock (_lock) {
                if (_disposed) return;
                if (_entries.TryGetValue(sid, out var e)) Begin(e);
            }
        });
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
            if (_disposed) return; // Dispose already completed and cleared every entry
            if (!_entries.TryGetValue(lease.SessionId, out var entry)) return;
            if (--entry.Leases > 0) return;
            _entries.Remove(lease.SessionId);
            _chatOwners.Remove(lease.SessionId);
            entry.Attempt++;
            entry.RetryTimer?.Dispose();
            gone = entry;
        }
        gone.State.OnCompleted();
        if (_connected) _ = GiveBackChatAsync(lease.SessionId);
    }

    /// The last lease's give-back, ordered behind the session's in-flight join and dropped when a
    /// fresh acquisition has claimed the session meanwhile: the membership is the session's, not
    /// the lease's, and the new entry is relying on it.
    async Task GiveBackChatAsync(string sessionId) {
        using (await _chatGates.EnterAsync(sessionId).ConfigureAwait(false)) {
            lock (_lock) {
                if (_disposed || _entries.ContainsKey(sessionId)) return;
            }
            await _lane.UnsubscribeFromChatAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    void OnLane(bool connected) {
        List<Entry> entries;
        lock (_lock) {
            if (_disposed) return;
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
        string? diagnostic;
        var subscribed = false;
        try {
            var watch = await _lane.RegisterSessionAccessWatchAsync(entry.SessionId, CancellationToken.None).ConfigureAwait(false);
            if (watch.Result == HubCallResult.Ok) {
                using (await _chatGates.EnterAsync(entry.SessionId).ConfigureAwait(false)) {
                    ClaimChat(entry, attempt);
                    var chat = await _lane.SubscribeToChatAsync(entry.SessionId, CancellationToken.None).ConfigureAwait(false);
                    verdict = Classify(chat);
                    diagnostic = Diagnose(entry.SessionId, chat);
                    subscribed = chat.Result == HubCallResult.Ok;
                }
            } else {
                verdict = Classify(watch);
                diagnostic = Diagnose(entry.SessionId, watch);
            }
        } catch (Exception ex) {
            verdict = SessionAccessState.Unavailable;
            diagnostic = $"kcap: session access for {entry.SessionId} failed: {ex.Message}";
        }

        // A superseded attempt that did join still holds the group unless a replacement claimed it.
        // Whether it does is settled inside the gate below, not here: between this lock and the
        // hub call a replacement can claim the session and join it.
        var staleJoin = false;
        string? report = null;
        lock (_lock) {
            if (_disposed || entry.Attempt != attempt) {
                staleJoin = subscribed;
            } else {
                // The retry ladder re-runs this every few seconds, so only a CHANGE of reason is
                // worth a line; a recovery re-arms the next one.
                if (diagnostic != entry.LastDiagnostic) report = diagnostic;
                entry.LastDiagnostic = diagnostic;
                Publish(entry, verdict);
                if (verdict != SessionAccessState.Unavailable || !_connected) {
                    entry.Failures = 0;
                } else {
                    var delay = Retry[Math.Min(entry.Failures++, Retry.Length - 1)];
                    entry.RetryTimer = _time.CreateTimer(_ => { lock (_lock) { if (entry.Attempt == attempt) Begin(entry); } }, null, delay, Timeout.InfiniteTimeSpan);
                }
            }
        }
        if (report is not null) Console.Error.WriteLine(report);
        if (staleJoin) await GiveBackStaleChatAsync(entry, attempt).ConfigureAwait(false);
    }

    /// A superseded attempt's give-back. The membership is the session's, not the attempt's:
    /// handing it back while a live entry relies on it stops the payloads with the lease still
    /// reading Established and no retry armed.
    async Task GiveBackStaleChatAsync(Entry entry, int attempt) {
        using (await _chatGates.EnterAsync(entry.SessionId).ConfigureAwait(false)) {
            lock (_lock) {
                if (!OwnsChat(entry, attempt)) return;
            }
            await _lane.UnsubscribeFromChatAsync(entry.SessionId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    // Claimed BEFORE the subscribe it describes, not after: a slower watch call would otherwise
    // let a superseded attempt record its claim over a newer attempt's.
    void ClaimChat(Entry entry, int attempt) {
        lock (_lock) {
            if (_disposed || entry.Attempt != attempt) return;
            _chatOwners[entry.SessionId] = (entry, attempt);
        }
    }

    // Caller holds _lock, inside the session's chat gate. The group is this attempt's to give back
    // only when no live entry holds the session, or when the claim on record is its own.
    bool OwnsChat(Entry entry, int attempt) {
        if (!_entries.ContainsKey(entry.SessionId)) return true;
        if (!_chatOwners.TryGetValue(entry.SessionId, out var owner) || !ReferenceEquals(owner.Entry, entry) || owner.Attempt != attempt) return false;
        _chatOwners.Remove(entry.SessionId);
        return true;
    }

    static SessionAccessState Classify(HubCallOutcome outcome) => outcome.Result switch {
        HubCallResult.Ok => SessionAccessState.Established,
        HubCallResult.Denied => SessionAccessState.Denied,
        _ => SessionAccessState.Unavailable,
    };

    /// NotConnected is the normal lane-down state and stays silent; everything else would
    /// otherwise present as "Not connected to the server" plus a silent retry loop.
    static string? Diagnose(string sessionId, HubCallOutcome outcome) => outcome.Result switch {
        HubCallResult.Failed => $"kcap: session access for {sessionId} failed: {outcome.Reason}",
        HubCallResult.Denied => $"kcap: session access for {sessionId} denied: {outcome.Reason}",
        _ => null,
    };

    // Caller holds _lock.
    void Publish(Entry entry, SessionAccessState state) {
        if (entry.State.Value == state && state != SessionAccessState.Establishing) return;
        entry.State.OnNext(state);
        _transitions.OnNext((entry.SessionId, state));
    }

    public void Dispose() {
        List<Entry> entries;
        lock (_lock) {
            if (_disposed) return;
            _disposed = true;
            entries = [.. _entries.Values];
            foreach (var e in entries) { e.Attempt++; e.RetryTimer?.Dispose(); }
            _entries.Clear();
            _chatOwners.Clear();
        }
        foreach (var e in entries) e.State.OnCompleted();
        _subscriptions.Dispose();
        _transitions.Dispose();
    }
}
