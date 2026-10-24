using Capacitor.App.Services;
using Capacitor.Cli.Core.MessageSenders;

namespace Capacitor.App.ViewModels;

/// Names the senders of one chat's user messages, read from the server for the chat's session. UI thread
/// only: labels are tracked and titled there, and only the read itself leaves it. One read at a time.
internal sealed class MessageSenderLookup(IMessageSenderSource source, TimeProvider time, CancellationToken lifetime, Action<string> log) {
    /// Gaps before asking again about a recent message still unnamed: the server records a sender a few
    /// seconds after the message lands. Older messages are asked about once.
    internal static readonly TimeSpan[] Retry = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)];
    internal static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(1);

    internal const string Caller = "You";
    internal const string Unnamed = "User";

    sealed class Entry(SenderLabel label, int retries) {
        public SenderLabel Label { get; } = label;
        public int Retries { get; } = retries;
        public int Attempt;
    }

    readonly Queue<Entry> _due = new();
    string? _sessionId;
    int _generation;
    bool _reading;

    /// The read in flight, or the last one started.
    internal Task? PendingReadForTesting { get; private set; }

    public void SetSession(string? sessionId) {
        if (_sessionId == sessionId) return;
        _sessionId = sessionId;
        Pump();
    }

    public void Track(IEnumerable<SenderLabel> labels) {
        var now = time.GetUtcNow();
        foreach (var label in labels) {
            if (label.Keys.Count == 0 || label.HasTitle) continue;
            var recent = label.SentAt is not { } at || now - at < RecentWindow;
            _due.Enqueue(new Entry(label, recent ? Retry.Length : 0));
        }
        Pump();
    }

    /// The rows are gone: nothing asked or waiting before now is applied or asked again.
    public void Clear() {
        _generation++;
        _due.Clear();
    }

    void Pump() {
        if (_reading || _due.Count == 0 || _sessionId is null || lifetime.IsCancellationRequested) return;
        _reading = true;
        PendingReadForTesting = ReadAsync(_generation);
    }

    async Task ReadAsync(int generation) {
        try {
            while (_due.Count > 0 && generation == _generation && _sessionId is { } session && !lifetime.IsCancellationRequested) {
                var batch = TakeBatch();
                var read = await source.ReadAsync(session, Request(batch), lifetime);
                if (generation != _generation) break;
                Apply(batch, read, generation);
                // The session is not readable as this viewer: asking about the rest would get the same answer.
                if (read.Kind is MessageSendersReadKind.Unavailable or MessageSendersReadKind.SignedOut) _due.Clear();
            }
        } catch (OperationCanceledException) when (lifetime.IsCancellationRequested) {
            return;
        } catch (Exception ex) {
            log($"message senders: {ex.Message}");
        } finally {
            _reading = false;
        }
        Pump();
    }

    List<Entry> TakeBatch() {
        var batch = new List<Entry>();
        var keys = 0;
        while (_due.TryPeek(out var next) && (batch.Count == 0 || keys + next.Label.Keys.Count <= MessageSendersRequest.MaxKeys)) {
            batch.Add(_due.Dequeue());
            keys += next.Label.Keys.Count;
        }
        return batch;
    }

    static MessageSendersRequest Request(List<Entry> batch) {
        var request = new MessageSendersRequest();
        foreach (var entry in batch) {
            var keys = entry.Label.Keys;
            if (keys.EventId is { } id) request.EventIds.Add(id);
            if (keys.EventNumber is { } number) request.EventNumbers.Add(number);
            if (keys.DispatchId is { } dispatch) request.DispatchIds.Add(dispatch);
        }
        return request;
    }

    void Apply(List<Entry> batch, MessageSendersRead read, int generation) {
        var byId = new Dictionary<Guid, MessageSenderDto>();
        var byNumber = new Dictionary<long, MessageSenderDto>();
        var byDispatch = new Dictionary<Guid, MessageSenderDto>();
        foreach (var row in read.Senders) {
            if (row.EventId is { } id) byId.TryAdd(id, row);
            if (row.EventNumber is { } number) byNumber.TryAdd(number, row);
            if (row.DispatchId is { } dispatch) byDispatch.TryAdd(dispatch, row);
        }
        var retry = new List<Entry>();
        foreach (var entry in batch) {
            var keys = entry.Label.Keys;
            var row = keys.EventId is { } id && byId.TryGetValue(id, out var r1) ? r1
                : keys.EventNumber is { } number && byNumber.TryGetValue(number, out var r2) ? r2
                : keys.DispatchId is { } dispatch && byDispatch.TryGetValue(dispatch, out var r3) ? r3
                : null;
            if (row is not null) entry.Label.Title = TitleOf(row);
            else if (read.Kind is MessageSendersReadKind.Ready or MessageSendersReadKind.Unreachable && entry.Attempt < entry.Retries)
                retry.Add(entry);
        }
        foreach (var group in retry.GroupBy(entry => entry.Attempt))
            _ = RetryAsync([.. group], Retry[group.Key], generation);
    }

    static string TitleOf(MessageSenderDto row) =>
        row.SentByCaller ? Caller : row.SenderName is { Length: > 0 } name ? name : Unnamed;

    async Task RetryAsync(List<Entry> entries, TimeSpan delay, int generation) {
        try { await Task.Delay(delay, time, lifetime); }
        catch (OperationCanceledException) { return; }
        if (generation != _generation) return;
        foreach (var entry in entries) {
            entry.Attempt++;
            _due.Enqueue(entry);
        }
        Pump();
    }
}
