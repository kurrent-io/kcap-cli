using Capacitor.App.Services;
using Capacitor.Cli.Core;

namespace Capacitor.App.ViewModels;

/// A subagent's own events on another machine, paged from the server. The hub tails only a session's
/// root stream, so a running subagent is polled at a slower pace than the chat reads, and an ended one
/// more slowly still.
internal sealed class RemoteSubagentFeed(
        string sessionId, string agentId, string vendor, SubagentEventsReader read, Func<bool> running,
        TimeProvider time, Action<string> log) : IChatTranscriptFeed {
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    /// Once its subagent has ended the feed still looks now and then: nothing bounds how long its last
    /// events can trail the parent's word that it ended.
    internal static readonly TimeSpan EndedPollInterval = TimeSpan.FromSeconds(30);
    const int MaxPagesPerRead = 20;

    readonly IChatDisplayRules? _rules = TranscriptChat.SubagentRulesFor(vendor);
    readonly CancellationTokenSource _lifetime = new();
    /// The server's `from` counts the events already received, not an event number: a stream whose oldest
    /// events have aged out starts above zero.
    long _next;
    long _lastEvent = -1;
    long? _position;
    string? _loggedFailure;
    DateTimeOffset? _fetchedAt;
    /// Reads made since the subagent was last seen running. Its last events can reach the server after
    /// the parent hears it ended, so one more read follows before the feed stops.
    int _readsSinceEnded;
    bool _complete;
    /// A refusal stands until the next fetch: the chat clears it on any other read, so a poll the
    /// interval skips repeats it.
    FeedRead _standing = new(FeedStatus.Ok, []);

    public long? CurrentOffset => _position;

    public FeedRead ReadAppended() {
        CancellationToken token;
        try { token = _lifetime.Token; } catch (ObjectDisposedException) { return new(FeedStatus.Ok, []); }
        if (token.IsCancellationRequested) return new(FeedStatus.Ok, []);
        var now = time.GetUtcNow();
        if (_fetchedAt is { } last && now - last < (_complete ? EndedPollInterval : PollInterval)) return _standing;
        _fetchedAt = now;
        var read = Fetch(token);
        _standing = read.Status is FeedStatus.Missing or FeedStatus.Failed ? read : new(FeedStatus.Ok, []);
        return read;
    }

    FeedRead Fetch(CancellationToken token) {
        // Read before fetching: a subagent that ended before this fetch began is complete once it lands.
        var ended = !running();
        var lines = new List<ProjectedLine>();
        try {
            for (var page = 0; page < MaxPagesPerRead; page++) {
                var fetch = read(sessionId, agentId, _next, token).GetAwaiter().GetResult();
                // The server admits the route once its subagent row projects, which can trail the start record.
                if (fetch.NotFound) {
                    // An ended subagent the server still cannot find counts toward completion, so its polls slow down.
                    if (ended) Settle(ended);
                    return new(lines.Count > 0 || !ended ? FeedStatus.Ok : FeedStatus.Missing, lines);
                }
                if (fetch.Unauthorized) return Failed(lines, "not authorized to read this subagent");
                if (fetch.Page is not { } dto) {
                    if (fetch.Failure is { } why && why != _loggedFailure) log($"subagent read: {why}");
                    _loggedFailure = fetch.Failure;
                    return Failed(lines, fetch.Failure is null ? "could not reach the server" : "could not read this subagent");
                }
                _loggedFailure = null;
                var from = _next;
                var received = 0;
                foreach (var evt in dto.Events ?? []) {
                    received++;
                    if (evt.EventNumber <= _lastEvent) continue;
                    _lastEvent = evt.EventNumber;
                    _position  = evt.EventNumber;
                    if (evt.Body is { } body && Project(evt.EventType, body.GetRawText(), evt.EventNumber, evt.Timestamp) is { } line)
                        lines.Add(line);
                }
                if (dto.NextFrom is not { } nextFrom) {
                    _next = from + received;
                    Settle(ended);
                    break;
                }
                _next = Math.Max(from + received, nextFrom);
            }
        } catch (OperationCanceledException) when (token.IsCancellationRequested) {
            return new(FeedStatus.Ok, []);
        } catch (Exception ex) {
            log($"subagent read: {ex.Message}");
            return Failed(lines, "could not read this subagent");
        }
        return new(FeedStatus.Ok, lines);
    }

    void Settle(bool ended) {
        _readsSinceEnded = ended ? _readsSinceEnded + 1 : 0;
        _complete = _readsSinceEnded > 1;
    }

    // A failure past a page already read keeps that page: the chat drops a failed read's lines, and the
    // next poll resumes after them.
    static FeedRead Failed(List<ProjectedLine> lines, string failure) =>
        lines.Count > 0 ? new(FeedStatus.Ok, lines) : new(FeedStatus.Failed, lines, Failure: failure);

    ProjectedLine? Project(string eventType, string json, long offset, DateTimeOffset? timestamp) {
        var payload = CanonicalEventJson.TryParse(eventType, json);
        if (payload is null) return null;
        var at = CanonicalEventTime.Of(payload) ?? timestamp ?? time.GetUtcNow();
        var projected = TranscriptChat.Project(new CanonicalEvent(eventType, payload, Guid.Empty, at), _rules);
        // A hidden record can still carry a run's end.
        return projected.Envelopes.Count == 0 && projected.SubmittedInputs.Count == 0 && projected.Runs.Count == 0
            ? null
            : new(projected, offset);
    }

    public void Dispose() {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
