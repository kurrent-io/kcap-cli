using Capacitor.App.Services;
using Capacitor.Cli.Core.MessageSenders;

namespace Capacitor.App.ViewModels;

/// Names the senders of one chat's user messages, read from the server for the chat's session. UI thread
/// only: labels are tracked and titled there, and only the read itself leaves it. One read at a time.
internal sealed class MessageSenderLookup(IMessageSenderSource source, TimeProvider time, CancellationToken lifetime, Action<string> log,
        Action<Guid, SenderLabel>? delivered = null) {
    /// Gaps between asks about one message. A recent message still unnamed is asked again on them, since the
    /// server records a sender a few seconds after the message lands; an older one only after a failed read.
    internal static readonly TimeSpan[] Retry = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)];
    internal static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(1);

    internal const string Caller = "You";
    internal const string Unnamed = "User";

    sealed class Entry(SenderLabel label, int unmatchedRetries) {
        public SenderLabel Label { get; } = label;
        public int UnmatchedRetries { get; } = unmatchedRetries;
        public int Unmatched;
        public int Failed;
    }

    readonly Queue<Entry> _due = new();
    readonly HashSet<Guid> _receipts = [];
    string? _sessionId;
    int _generation;
    bool _reading;

    /// The read in flight, or the last one started.
    internal Task? PendingReadForTesting { get; private set; }

    /// A sender attribution exists only after the server has matched a transcript message.
    public bool HasReceipt(Guid dispatchId) => _receipts.Contains(dispatchId);

    /// A title names a sender of the session the chat showed when it was read, so another session starts the
    /// visible messages over.
    public void SetSession(string? sessionId, IEnumerable<SenderLabel> visible) {
        if (_sessionId == sessionId) return;
        _sessionId = sessionId;
        _receipts.Clear();
        Restart(visible);
    }

    /// Forgets every title and asks about the visible messages again: after a sign-in the viewer may be
    /// another person, whose "You" is someone else's. Also how messages left unasked while the session was
    /// unreadable are asked about once it may be readable.
    public void Restart(IEnumerable<SenderLabel> visible) {
        var labels = visible.ToList();
        _generation++;
        _due.Clear();
        foreach (var label in labels) label.Title = null;
        Track(labels);
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

    bool IsCurrent(int generation, string session) => generation == _generation && _sessionId == session;

    async Task ReadAsync(int generation) {
        try {
            while (_due.Count > 0 && _sessionId is { } session && IsCurrent(generation, session) && !lifetime.IsCancellationRequested) {
                var batch = TakeBatch();
                var read = await source.ReadAsync(session, Request(batch), lifetime);
                if (!IsCurrent(generation, session) || lifetime.IsCancellationRequested) break;
                Apply(batch, read, generation, session);
                // Asking about the rest would get the same answer. They stay on screen untitled, and the next
                // sign-in or session change asks about them again.
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

    void Apply(List<Entry> batch, MessageSendersRead read, int generation, string session) {
        var byId = new Dictionary<Guid, MessageSenderDto>();
        var byNumber = new Dictionary<long, MessageSenderDto>();
        var byDispatch = new Dictionary<Guid, MessageSenderDto>();
        foreach (var row in read.Senders) {
            if (row.EventId is { } id) byId.TryAdd(id, row);
            if (row.EventNumber is { } number) byNumber.TryAdd(number, row);
            if (row.DispatchId is { } dispatch) byDispatch.TryAdd(dispatch, row);
        }
        var retry = new List<(Entry Entry, TimeSpan Delay)>();
        foreach (var entry in batch) {
            var keys = entry.Label.Keys;
            var row = keys.EventId is { } id && byId.TryGetValue(id, out var r1) ? r1
                : keys.EventNumber is { } number && byNumber.TryGetValue(number, out var r2) ? r2
                : keys.DispatchId is { } dispatch && byDispatch.TryGetValue(dispatch, out var r3) ? r3
                : null;
            if (row is not null) {
                entry.Label.Title = TitleOf(row);
                if (read.Kind == MessageSendersReadKind.Ready && row.DispatchId is { } receipt
                    && receipt != Guid.Empty && _receipts.Add(receipt)) delivered?.Invoke(receipt, entry.Label);
            }
            else if (read.Kind == MessageSendersReadKind.Ready && entry.Unmatched < entry.UnmatchedRetries)
                retry.Add((entry, Retry[entry.Unmatched++]));
            else if (read.Kind == MessageSendersReadKind.Unreachable && entry.Failed < Retry.Length)
                retry.Add((entry, Retry[entry.Failed++]));
        }
        foreach (var group in retry.GroupBy(r => r.Delay))
            _ = RetryAsync([.. group.Select(r => r.Entry)], group.Key, generation, session);
    }

    static string TitleOf(MessageSenderDto row) =>
        row.SentByCaller ? Caller : row.SenderName is { Length: > 0 } name ? name : Unnamed;

    async Task RetryAsync(List<Entry> entries, TimeSpan delay, int generation, string session) {
        try { await Task.Delay(delay, time, lifetime); }
        catch (OperationCanceledException) { return; }
        if (!IsCurrent(generation, session)) return;
        foreach (var entry in entries) _due.Enqueue(entry);
        Pump();
    }
}
