using System.Collections.Concurrent;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Remote.Models;
using Microsoft.AspNetCore.SignalR;

namespace Capacitor.App.ViewModels;

/// The chat rows of a session on another machine: a seed from the session detail route, then a
/// live tail of the session's stream from the position the seed ended at. Every access
/// establishment restarts the tail from the last position seen; the seed is fetched only until
/// one lands, so a reconnect resumes rather than replays. Rows queue here and the pane's poll
/// drains them.
internal sealed class RemoteTranscriptFeed : IChatTranscriptFeed {
    /// Gaps between tails that ended while access still stood; each further one is the next entry.
    internal static readonly TimeSpan[] Retry = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    readonly string _sessionId;
    readonly IChatDisplayRules? _rules;
    readonly SessionDetailReader _readDetail;
    readonly IServerLane _lane;
    readonly TimeProvider _time;
    readonly Action<string> _log;
    readonly ConcurrentDictionary<string, byte> _logged = new(StringComparer.Ordinal);
    readonly Lock _lock = new();
    readonly List<ProjectedLine> _pending = new();
    readonly IDisposable _access;
    readonly CancellationTokenSource _lifetime = new();
    FeedStatus _pendingStatus = FeedStatus.Ok;
    /// A Missing/Failed verdict, held apart from the seed's own rows so a refusal that lands
    /// after a successful seed never displaces the seed's still-undrained Reset.
    (FeedStatus Status, string? Failure)? _pendingFailure;
    /// The last event number applied; null until the seed lands.
    long? _position;
    int _attempt;
    CancellationTokenSource? _tailCts;
    volatile bool _waitingToRetry;
    bool _disposed;

    internal Task? PendingRunForTesting { get; private set; }
    internal bool WaitingToRetryForTesting => _waitingToRetry;

    public RemoteTranscriptFeed(
            string sessionId, string vendor, IObservable<SessionAccessState> access, SessionDetailReader readDetail,
            IServerLane lane, TimeProvider time, Action<string> log) {
        _sessionId = sessionId;
        _rules = TranscriptChat.RulesFor(vendor);
        _readDetail = readDetail;
        _lane = lane;
        _time = time;
        _log = log;
        _access = access.Subscribe(state => {
            if (state == SessionAccessState.Established) Restart();
            else StopTail();
        });
    }

    public long? CurrentOffset {
        get { lock (_lock) return _position is { } p ? p + 1 : null; }
    }

    public FeedRead ReadAppended() {
        lock (_lock) {
            if (_pendingStatus == FeedStatus.Reset || _pending.Count > 0) {
                var status = _pendingStatus;
                var lines = _pending.Count == 0 ? [] : _pending.ToArray();
                _pending.Clear();
                _pendingStatus = FeedStatus.Ok;
                return new(status, lines, status == FeedStatus.Reset ? CurrentOffsetLocked() : null);
            }
            if (_pendingFailure is { } pending) {
                _pendingFailure = null;
                return new(pending.Status, [], null, pending.Failure);
            }
            return new(FeedStatus.Ok, []);
        }
    }

    long? CurrentOffsetLocked() => _position is { } p ? p + 1 : null;

    void Restart() {
        lock (_lock) {
            if (_disposed) return;
            _tailCts?.Cancel();
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _tailCts = cts;
            var attempt = ++_attempt;
            var run = Task.Run(() => RunAsync(attempt, cts.Token));
            PendingRunForTesting = run.ContinueWith(_ => {
                lock (_lock) { if (ReferenceEquals(_tailCts, cts)) _tailCts = null; }
                cts.Dispose();
            }, TaskScheduler.Default);
        }
    }

    void StopTail() {
        lock (_lock) {
            _tailCts?.Cancel();
            _tailCts = null;
            _attempt++;
        }
    }

    bool IsCurrent(int attempt) { lock (_lock) return _attempt == attempt; }

    async Task RunAsync(int attempt, CancellationToken ct) {
        var failures = 0;
        while (!ct.IsCancellationRequested && IsCurrent(attempt)) {
            try {
                if (!await EnsureSeededAsync(attempt, ct).ConfigureAwait(false)) return;
                if (await TailAsync(attempt, ct).ConfigureAwait(false)) failures = 0;
            } catch (OperationCanceledException) {
                return;
            } catch (HubException ex) when (ex.Message.Contains(WireTokens.StreamNotAuthorized, StringComparison.Ordinal)) {
                Enqueue(FeedStatus.Failed, "not authorized to read this session's stream");
                return;
            } catch (Exception ex) {
                LogOnce($"remote transcript: {ex.Message}");
            }
            // The tail ended without a cancel: the connection dropped, or a newer subscribe on the
            // same stream replaced it. A superseded attempt stops; the current one waits and resumes.
            if (!IsCurrent(attempt)) return;
            var delay = Retry[Math.Min(failures++, Retry.Length - 1)];
            var wait = Task.Delay(delay, _time, ct);
            _waitingToRetry = true;
            try { await wait.ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            finally { _waitingToRetry = false; }
        }
    }

    /// True once a seed is in place. A hidden session is Missing and stops the run; an
    /// unauthorized read leaves the pane waiting for the sign-in the host asks for.
    async Task<bool> EnsureSeededAsync(int attempt, CancellationToken ct) {
        lock (_lock) if (_position is not null) return true;
        var fetch = await _readDetail(_sessionId, ct).ConfigureAwait(false);
        if (!IsCurrent(attempt)) return false;
        if (fetch.NotFound) { Enqueue(FeedStatus.Missing); return false; }
        if (fetch.Unauthorized) return false;
        if (fetch.Detail is not { } detail) throw new InvalidOperationException("session detail unavailable");

        var lines = new List<ProjectedLine>();
        foreach (var evt in detail.Events ?? [])
            if (evt.Body is { } body && Project(evt.EventType, body.GetRawText(), evt.EventNumber, evt.Timestamp) is { } line) lines.Add(line);
        lock (_lock) {
            if (_attempt != attempt) return false;
            _position = detail.LastEventNumber;
            _pending.Clear();
            _pending.AddRange(lines);
            _pendingStatus = FeedStatus.Reset;
        }
        return true;
    }

    /// True when the tail delivered at least one envelope, which is what resets the retry ladder;
    /// a tail that ends without ever yielding keeps the caller's failure count climbing.
    async Task<bool> TailAsync(int attempt, CancellationToken ct) {
        ulong? from;
        lock (_lock) from = _position is >= 0 ? (ulong)_position : null;
        var received = false;
        await foreach (var envelope in _lane.TailStreamAsync(StreamNames.AgentSession(_sessionId), from, ct).ConfigureAwait(false)) {
            received = true;
            var line = Project(envelope.EventType, envelope.JsonPayload, (long)envelope.StreamPosition, envelope.Timestamp);
            lock (_lock) {
                if (_attempt != attempt) return received;
                _position = (long)envelope.StreamPosition;
                if (line is { } l) _pending.Add(l);
            }
        }
        return received;
    }

    ProjectedLine? Project(string eventType, string json, long offset, DateTimeOffset? timestamp) {
        var payload = CanonicalEventJson.TryParse(eventType, json);
        if (payload is null) return null;
        var projected = TranscriptChat.Project(new CanonicalEvent(eventType, payload, Guid.Empty, timestamp ?? _time.GetUtcNow()), _rules);
        return projected.Envelopes.Count == 0 && projected.SubmittedInputs.Count == 0 ? null : new(projected, offset);
    }

    void Enqueue(FeedStatus status, string? failure = null) {
        lock (_lock) { _pendingFailure = (status, failure); }
    }

    void LogOnce(string reason) {
        if (_logged.TryAdd(reason, 0)) _log(reason);
    }

    public void Dispose() {
        lock (_lock) {
            if (_disposed) return;
            _disposed = true;
            _tailCts?.Cancel();
            _tailCts = null;
            _attempt++;
        }
        _access.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
