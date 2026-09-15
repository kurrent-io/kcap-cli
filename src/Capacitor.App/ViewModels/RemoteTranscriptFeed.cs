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
    string? _failure;
    /// The last event number applied; null until the seed lands.
    long? _position;
    int _attempt;
    CancellationTokenSource? _tailCts;
    volatile bool _waitingToRetry;

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
            var status = _pendingStatus;
            var failure = _failure;
            var lines = _pending.Count == 0 ? [] : _pending.ToArray();
            _pending.Clear();
            _pendingStatus = FeedStatus.Ok;
            _failure = null;
            return new(status, lines, status == FeedStatus.Reset ? CurrentOffsetLocked() : null, failure);
        }
    }

    long? CurrentOffsetLocked() => _position is { } p ? p + 1 : null;

    void Restart() {
        int attempt;
        CancellationTokenSource cts;
        lock (_lock) {
            if (_lifetime.IsCancellationRequested) return;
            _tailCts?.Cancel();
            _tailCts = cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            attempt = ++_attempt;
        }
        PendingRunForTesting = Task.Run(() => RunAsync(attempt, cts.Token));
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
                await TailAsync(attempt, ct).ConfigureAwait(false);
                failures = 0;
            } catch (OperationCanceledException) {
                return;
            } catch (HubException ex) when (ex.Message.Contains(WireTokens.StreamNotAuthorized, StringComparison.Ordinal)) {
                Enqueue(FeedStatus.Failed, [], "not authorized to read this session's stream");
                return;
            } catch (Exception ex) {
                LogOnce($"remote transcript: {ex.Message}");
            }
            // The tail ended without a cancel: the connection dropped, or a newer subscribe on the
            // same stream replaced it. A superseded attempt stops; the current one waits and resumes.
            if (!IsCurrent(attempt)) return;
            var delay = Retry[Math.Min(failures++, Retry.Length - 1)];
            _waitingToRetry = true;
            try { await Task.Delay(delay, _time, ct).ConfigureAwait(false); }
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
        if (fetch.NotFound) { Enqueue(FeedStatus.Missing, []); return false; }
        if (fetch.Unauthorized) return false;
        if (fetch.Detail is not { } detail) throw new InvalidOperationException("session detail unavailable");

        var lines = new List<ProjectedLine>();
        foreach (var evt in detail.Events ?? [])
            if (evt.Body is { } body && Project(evt.EventType, body.GetRawText(), evt.EventNumber) is { } line) lines.Add(line);
        lock (_lock) {
            if (_attempt != attempt) return false;
            _position = detail.LastEventNumber;
            _pending.Clear();
            _pending.AddRange(lines);
            _pendingStatus = FeedStatus.Reset;
            _failure = null;
        }
        return true;
    }

    async Task TailAsync(int attempt, CancellationToken ct) {
        ulong? from;
        lock (_lock) from = _position is >= 0 ? (ulong)_position : null;
        await foreach (var envelope in _lane.TailStreamAsync(StreamNames.AgentSession(_sessionId), from, ct).ConfigureAwait(false)) {
            var line = Project(envelope.EventType, envelope.JsonPayload, (long)envelope.StreamPosition);
            lock (_lock) {
                if (_attempt != attempt) return;
                _position = (long)envelope.StreamPosition;
                if (line is { } l) _pending.Add(l);
            }
        }
    }

    ProjectedLine? Project(string eventType, string json, long offset) {
        var payload = CanonicalEventJson.TryParse(eventType, json);
        if (payload is null) return null;
        var projected = TranscriptChat.Project(new CanonicalEvent(eventType, payload, Guid.Empty, _time.GetUtcNow()), _rules);
        return projected.Envelopes.Count == 0 && projected.SubmittedInputs.Count == 0 ? null : new(projected, offset);
    }

    void Enqueue(FeedStatus status, IReadOnlyList<ProjectedLine> lines, string? failure = null) {
        lock (_lock) {
            _pendingStatus = status;
            _failure = failure;
            _pending.AddRange(lines);
        }
    }

    void LogOnce(string reason) {
        if (_logged.TryAdd(reason, 0)) _log(reason);
    }

    public void Dispose() {
        _access.Dispose();
        StopTail();
        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
        _lifetime.Dispose();
    }
}
