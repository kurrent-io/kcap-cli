using Avalonia.Threading;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.MessageSenders;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

/// What the lookup asks the server for and how an answer titles each message. Runs on the UI thread, where the
/// lookup's continuations resume.
public class MessageSenderLookupTests {
    const string Session = "s1";

    static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    sealed class Rig {
        public FakeTimeProvider Time { get; } = new(Now);
        public FakeMessageSenderSource Source { get; } = new();
        public List<string> Logged { get; } = [];
        public List<Guid> Delivered { get; } = [];
        public CancellationTokenSource Lifetime { get; } = new();
        public MessageSenderLookup Lookup { get; }

        public Rig() => Lookup = new MessageSenderLookup(Source, Time, Lifetime.Token, Logged.Add, (id, _) => Delivered.Add(id));

        public async Task SettleAsync() {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (Lookup.PendingReadForTesting is { } read) await read;
        }
    }

    static SenderLabel Old(MessageKeys keys) => new(keys, Now - TimeSpan.FromHours(1));
    static SenderLabel Recent(MessageKeys keys) => new(keys, Now);

    static MessageSendersRead Ready(params MessageSenderDto[] rows) => new(MessageSendersReadKind.Ready, rows);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel("AvaloniaSession")]
    public Task A_positive_receipt_survives_a_same_session_refresh_but_not_a_session_change(bool restart) => RunOnUiAsync(async () => {
        var rig = new Rig();
        var eventId = Guid.NewGuid();
        var dispatch = Guid.NewGuid();
        var label = Old(new(eventId));
        rig.Source.Answer = _ => Ready(new MessageSenderDto { EventId = eventId, DispatchId = dispatch });
        rig.Lookup.SetSession(Session, []);
        rig.Lookup.Track([label]);
        await rig.SettleAsync();
        await Assert.That(rig.Lookup.HasReceipt(dispatch)).IsTrue();
        rig.Source.Answer = _ => MessageSendersRead.Of(MessageSendersReadKind.Unreachable);

        if (restart) rig.Lookup.Restart([label]);
        else rig.Lookup.Clear();
        await rig.SettleAsync();

        await Assert.That(rig.Lookup.HasReceipt(dispatch)).IsTrue();
        rig.Lookup.SetSession("other", []);
        await Assert.That(rig.Lookup.HasReceipt(dispatch)).IsFalse();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_read_finishing_after_cancellation_neither_titles_nor_confirms_a_message() => RunOnUiAsync(async () => {
        var rig = new Rig();
        var eventId = Guid.NewGuid();
        var label = Old(new(eventId));
        rig.Source.Answer = _ => {
            rig.Lifetime.Cancel();
            return Ready(new MessageSenderDto { EventId = eventId, DispatchId = Guid.NewGuid(), SentByCaller = true });
        };
        rig.Lookup.SetSession(Session, []);

        rig.Lookup.Track([label]);
        await rig.SettleAsync();

        await Assert.That(label.HasTitle).IsFalse();
        await Assert.That(rig.Delivered).IsEmpty();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_sender_reads_by_name_the_caller_as_you_and_an_unrecorded_message_keeps_the_fallback() => RunOnUiAsync(async () => {
        var rig = new Rig();
        Guid named = Guid.NewGuid(), mine = Guid.NewGuid(), nameless = Guid.NewGuid(), unrecorded = Guid.NewGuid();
        rig.Source.Answer = _ => Ready(
            new() { EventId = named, SenderUserId = "github:1", SenderName = "Tony" },
            new() { EventId = mine, SenderUserId = "github:2", SenderName = "Alexey", SentByCaller = true },
            new() { EventId = nameless, SenderUserId = "github:3" });
        SenderLabel[] labels = [Old(new(named)), Old(new(mine)), Old(new(nameless)), Old(new(unrecorded))];
        rig.Lookup.SetSession(Session, []);

        rig.Lookup.Track(labels);
        await rig.SettleAsync();

        await Assert.That(labels.Select(l => l.Title)).IsEquivalentTo(new[] { "Tony", "You", "User", null }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(labels[3].HasTitle).IsFalse();
        await Assert.That(rig.Source.Reads.Single().SessionId).IsEqualTo(Session);
        await Assert.That(rig.Source.Reads.Single().Request.EventIds).IsEquivalentTo(new[] { named, mine, nameless, unrecorded });
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_row_titles_a_message_through_any_key_the_message_holds() => RunOnUiAsync(async () => {
        var rig = new Rig();
        Guid eventId = Guid.NewGuid(), dispatchId = Guid.NewGuid();
        rig.Source.Answer = _ => Ready(
            new() { EventNumber = 7, EventId = eventId, SenderName = "By number" },
            new() { DispatchId = dispatchId, SenderName = "By dispatch" },
            new() { EventNumber = 9, EventId = Guid.NewGuid(), DispatchId = Guid.NewGuid(), SenderName = "Someone else" });
        SenderLabel byNumber = Old(new(EventNumber: 7)), byDispatch = Old(new(DispatchId: dispatchId)), byNothing = Old(new(EventNumber: 8));
        rig.Lookup.SetSession(Session, []);

        rig.Lookup.Track([byNumber, byDispatch, byNothing]);
        await rig.SettleAsync();

        await Assert.That(byNumber.Title).IsEqualTo("By number");
        await Assert.That(byDispatch.Title).IsEqualTo("By dispatch");
        await Assert.That(byNothing.Title).IsNull();
        var request = rig.Source.Reads.Single().Request;
        await Assert.That(request.EventNumbers).IsEquivalentTo(new long[] { 7, 8 });
        await Assert.That(request.DispatchIds).IsEquivalentTo(new[] { dispatchId });
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_recent_message_is_asked_again_until_its_sender_is_recorded() => RunOnUiAsync(async () => {
        var rig = new Rig();
        var id = Guid.NewGuid();
        var label = Recent(new(id));
        rig.Source.Answer = _ => rig.Source.Reads.Count < 3 ? Ready() : Ready(new MessageSenderDto { EventId = id, SenderName = "Tony" });
        rig.Lookup.SetSession(Session, []);

        rig.Lookup.Track([label]);
        await rig.SettleAsync();
        await Assert.That(label.Title).IsNull();

        rig.Time.Advance(MessageSenderLookup.Retry[0]);
        await WaitUntilAsync(() => rig.Source.Reads.Count == 2, what: "the first retry");
        await rig.SettleAsync();
        rig.Time.Advance(MessageSenderLookup.Retry[1]);
        await WaitUntilAsync(() => rig.Source.Reads.Count == 3, what: "the second retry");
        await rig.SettleAsync();

        await Assert.That(label.Title).IsEqualTo("Tony");
        rig.Time.Advance(TimeSpan.FromMinutes(5));
        await rig.SettleAsync();
        await Assert.That(rig.Source.Reads.Count).IsEqualTo(3);
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_recent_message_nobody_is_recorded_as_sending_stops_after_the_last_retry() => RunOnUiAsync(async () => {
        var rig = new Rig();
        rig.Lookup.SetSession(Session, []);

        rig.Lookup.Track([Recent(new(Guid.NewGuid()))]);
        await rig.SettleAsync();
        for (var i = 0; i < MessageSenderLookup.Retry.Length; i++) {
            rig.Time.Advance(MessageSenderLookup.Retry[i]);
            var expected = i + 2;
            await WaitUntilAsync(() => rig.Source.Reads.Count == expected, what: $"retry {i + 1}");
            await rig.SettleAsync();
        }
        rig.Time.Advance(TimeSpan.FromMinutes(5));
        await rig.SettleAsync();

        await Assert.That(rig.Source.Reads.Count).IsEqualTo(1 + MessageSenderLookup.Retry.Length);
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task History_is_asked_about_once() => RunOnUiAsync(async () => {
        var rig = new Rig();
        rig.Lookup.SetSession(Session, []);

        rig.Lookup.Track([Old(new(Guid.NewGuid()))]);
        await rig.SettleAsync();
        rig.Time.Advance(TimeSpan.FromMinutes(5));
        await rig.SettleAsync();

        await Assert.That(rig.Source.Reads.Count).IsEqualTo(1);
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Nothing_is_asked_until_the_session_is_known() => RunOnUiAsync(async () => {
        var rig = new Rig();
        var label = Old(new(Guid.NewGuid()));

        rig.Lookup.Track([label]);
        await rig.SettleAsync();
        await Assert.That(rig.Source.Reads).IsEmpty();

        rig.Lookup.SetSession(Session, [label]);
        await rig.SettleAsync();
        await Assert.That(rig.Source.Reads.Single().SessionId).IsEqualTo(Session);
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(MessageSendersReadKind.Unavailable)]
    [Arguments(MessageSendersReadKind.SignedOut)]
    public Task A_session_this_viewer_cannot_read_is_not_asked_again(MessageSendersReadKind kind) => RunOnUiAsync(async () => {
        var rig = new Rig();
        rig.Source.Answer = _ => MessageSendersRead.Of(kind);
        rig.Lookup.SetSession(Session, []);

        rig.Lookup.Track([Recent(new(Guid.NewGuid()))]);
        await rig.SettleAsync();
        rig.Time.Advance(TimeSpan.FromMinutes(5));
        await rig.SettleAsync();

        await Assert.That(rig.Source.Reads.Count).IsEqualTo(1);
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_cleared_chat_drops_its_pending_retries() => RunOnUiAsync(async () => {
        var rig = new Rig();
        rig.Lookup.SetSession(Session, []);

        rig.Lookup.Track([Recent(new(Guid.NewGuid()))]);
        await rig.SettleAsync();
        rig.Lookup.Clear();
        rig.Time.Advance(TimeSpan.FromMinutes(5));
        await rig.SettleAsync();

        await Assert.That(rig.Source.Reads.Count).IsEqualTo(1);
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Keys_past_the_servers_cap_go_in_a_further_request() => RunOnUiAsync(async () => {
        var rig = new Rig();
        rig.Lookup.SetSession(Session, []);

        rig.Lookup.Track(Enumerable.Range(0, MessageSendersRequest.MaxKeys + 1).Select(n => Old(new(EventNumber: n))).ToList());
        await WaitUntilAsync(() => rig.Source.Reads.Count == 2, what: "the second request");
        await rig.SettleAsync();

        await Assert.That(rig.Source.Reads.Select(r => r.Request.EventNumbers.Count)).IsEquivalentTo(new[] { MessageSendersRequest.MaxKeys, 1 });
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_failed_read_asks_about_history_again_on_the_backoff_and_then_stops() => RunOnUiAsync(async () => {
        var rig = new Rig();
        rig.Source.Answer = _ => MessageSendersRead.Of(MessageSendersReadKind.Unreachable);
        rig.Lookup.SetSession(Session, []);

        rig.Lookup.Track([Old(new(Guid.NewGuid()))]);
        await rig.SettleAsync();
        for (var i = 0; i < MessageSenderLookup.Retry.Length; i++) {
            rig.Time.Advance(MessageSenderLookup.Retry[i]);
            var expected = i + 2;
            await WaitUntilAsync(() => rig.Source.Reads.Count == expected, what: $"retry {i + 1}");
            await rig.SettleAsync();
        }
        rig.Time.Advance(TimeSpan.FromMinutes(5));
        await rig.SettleAsync();

        await Assert.That(rig.Source.Reads.Count).IsEqualTo(1 + MessageSenderLookup.Retry.Length);
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_read_that_fails_once_still_names_an_old_message() => RunOnUiAsync(async () => {
        var rig = new Rig();
        var id = Guid.NewGuid();
        var label = Old(new(id));
        rig.Source.Answer = _ => rig.Source.Reads.Count == 1
            ? MessageSendersRead.Of(MessageSendersReadKind.Unreachable)
            : Ready(new MessageSenderDto { EventId = id, SenderName = "Tony" });
        rig.Lookup.SetSession(Session, []);

        rig.Lookup.Track([label]);
        await rig.SettleAsync();
        rig.Time.Advance(MessageSenderLookup.Retry[0]);
        await WaitUntilAsync(() => rig.Source.Reads.Count == 2, what: "the retry");
        await rig.SettleAsync();

        await Assert.That(label.Title).IsEqualTo("Tony");
    });

    /// A read for the old session that lands after the switch must title nothing, and its retries must not run.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_session_change_drops_the_old_sessions_answer_and_retries_and_asks_again_under_the_new_one() => RunOnUiAsync(async () => {
        var rig = new Rig();
        Guid named = Guid.NewGuid(), pending = Guid.NewGuid();
        SenderLabel namedLabel = Old(new(named)), pendingLabel = Recent(new(pending));
        rig.Source.Answer = _ => {
            if (rig.Source.Reads.Count == 1) {
                rig.Lookup.SetSession("s2", [namedLabel, pendingLabel]);
                return Ready(new MessageSenderDto { EventId = named, SenderName = "Late old" });
            }
            return Ready(new MessageSenderDto { EventId = pending, SenderName = "Pending new" });
        };
        rig.Lookup.SetSession(Session, []);

        rig.Lookup.Track([namedLabel, pendingLabel]);
        await WaitUntilAsync(() => rig.Source.Reads.Count == 2, what: "the new session's read");
        await rig.SettleAsync();

        await Assert.That(namedLabel.HasTitle).IsFalse();
        await Assert.That(pendingLabel.Title).IsEqualTo("Pending new");
        await Assert.That(rig.Source.Reads.Select(r => r.SessionId)).IsEquivalentTo(new[] { Session, "s2" }, TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(rig.Source.Reads[1].Request.EventIds).IsEquivalentTo(new[] { named, pending });
        rig.Time.Advance(TimeSpan.FromMinutes(5));
        await rig.SettleAsync();
        await rig.SettleAsync();
        await Assert.That(rig.Source.Reads.Count).IsEqualTo(2);
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(MessageSendersReadKind.Unavailable)]
    [Arguments(MessageSendersReadKind.SignedOut)]
    public Task A_restart_asks_again_about_messages_an_unreadable_session_left_and_forgets_old_titles(MessageSendersReadKind kind) => RunOnUiAsync(async () => {
        var rig = new Rig();
        Guid mine = Guid.NewGuid(), left = Guid.NewGuid();
        SenderLabel mineLabel = Old(new(mine)), leftLabel = Old(new(left));
        rig.Source.Answer = _ => Ready(new MessageSenderDto { EventId = mine, SentByCaller = true });
        rig.Lookup.SetSession(Session, []);
        rig.Lookup.Track([mineLabel]);
        await rig.SettleAsync();
        rig.Source.Answer = _ => MessageSendersRead.Of(kind);
        rig.Lookup.Track([leftLabel]);
        await rig.SettleAsync();
        await Assert.That(mineLabel.Title).IsEqualTo("You");
        await Assert.That(leftLabel.HasTitle).IsFalse();

        rig.Source.Answer = _ => Ready(
            new MessageSenderDto { EventId = mine, SenderName = "Alexey" }, new MessageSenderDto { EventId = left, SenderName = "Tony" });
        rig.Lookup.Restart([mineLabel, leftLabel]);
        await rig.SettleAsync();

        await Assert.That(mineLabel.Title).IsEqualTo("Alexey");
        await Assert.That(leftLabel.Title).IsEqualTo("Tony");
    });
}
