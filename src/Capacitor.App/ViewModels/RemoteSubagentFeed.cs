using Capacitor.App.Services;
using Capacitor.Cli.Core;

namespace Capacitor.App.ViewModels;

/// A subagent's own events on another machine, paged from the server. The hub tails only a session's
/// root stream, so a running subagent is polled at a slower pace than the chat reads; once it has ended
/// and its last page is in, nothing more is fetched.
internal sealed class RemoteSubagentFeed(
        string sessionId, string agentId, string vendor, SubagentEventsReader read, Func<bool> running,
        TimeProvider time, Action<string> log) : IChatTranscriptFeed {
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    const int MaxPagesPerRead = 20;

    readonly IChatDisplayRules? _rules = TranscriptChat.SubagentRulesFor(vendor);
    readonly CancellationTokenSource _lifetime = new();
    long _next;
    long? _position;
    DateTimeOffset? _fetchedAt;
    bool _complete;

    public long? CurrentOffset => _position;

    public FeedRead ReadAppended() {
        CancellationToken token;
        try { token = _lifetime.Token; } catch (ObjectDisposedException) { return new(FeedStatus.Ok, []); }
        if (_complete || token.IsCancellationRequested) return new(FeedStatus.Ok, []);
        var now = time.GetUtcNow();
        if (_fetchedAt is { } last && now - last < PollInterval) return new(FeedStatus.Ok, []);
        _fetchedAt = now;
        // Read before fetching: a subagent that ended before this fetch began is complete once it lands.
        var ended = !running();
        var lines = new List<ProjectedLine>();
        try {
            for (var page = 0; page < MaxPagesPerRead; page++) {
                var fetch = read(sessionId, agentId, _next, token).GetAwaiter().GetResult();
                if (fetch.NotFound) return new(lines.Count > 0 ? FeedStatus.Ok : FeedStatus.Missing, lines);
                if (fetch.Unauthorized) return new(FeedStatus.Failed, lines, Failure: "not authorized to read this subagent");
                if (fetch.Page is not { } dto) return new(FeedStatus.Failed, lines, Failure: "could not reach the server");
                foreach (var evt in dto.Events ?? []) {
                    if (evt.EventNumber < _next) continue;
                    _next     = evt.EventNumber + 1;
                    _position = evt.EventNumber;
                    if (evt.Body is { } body && Project(evt.EventType, body.GetRawText(), evt.EventNumber, evt.Timestamp) is { } line)
                        lines.Add(line);
                }
                if (dto.NextFrom is not { } nextFrom) {
                    _complete = ended;
                    break;
                }
                _next = Math.Max(_next, nextFrom);
            }
        } catch (OperationCanceledException) when (token.IsCancellationRequested) {
            return new(FeedStatus.Ok, []);
        } catch (Exception ex) {
            log($"subagent read: {ex.Message}");
            return new(FeedStatus.Failed, lines, Failure: "could not read this subagent");
        }
        return new(FeedStatus.Ok, lines);
    }

    ProjectedLine? Project(string eventType, string json, long offset, DateTimeOffset? timestamp) {
        var payload = CanonicalEventJson.TryParse(eventType, json);
        if (payload is null) return null;
        var at = CanonicalEventTime.Of(payload) ?? timestamp ?? time.GetUtcNow();
        var projected = TranscriptChat.Project(new CanonicalEvent(eventType, payload, Guid.Empty, at), _rules);
        return projected.Envelopes.Count == 0 && projected.SubmittedInputs.Count == 0 ? null : new(projected, offset);
    }

    public void Dispose() {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
