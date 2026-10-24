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
        public CancellationTokenSource Lifetime { get; } = new();
        public MessageSenderLookup Lookup { get; }

        public Rig() => Lookup = new MessageSenderLookup(Source, Time, Lifetime.Token, Logged.Add);

        public async Task SettleAsync() {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (Lookup.PendingReadForTesting is { } read) await read;
        }
    }

    static SenderLabel Old(MessageKeys keys) => new(keys, Now - TimeSpan.FromHours(1));
    static SenderLabel Recent(MessageKeys keys) => new(keys, Now);

    static MessageSendersRead Ready(params MessageSenderDto[] rows) => new(MessageSendersReadKind.Ready, rows);

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
        rig.Lookup.SetSession(Session);

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
        rig.Lookup.SetSession(Session);

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
        rig.Lookup.SetSession(Session);

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
        rig.Lookup.SetSession(Session);

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
        rig.Lookup.SetSession(Session);

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

        rig.Lookup.Track([Old(new(Guid.NewGuid()))]);
        await rig.SettleAsync();
        await Assert.That(rig.Source.Reads).IsEmpty();

        rig.Lookup.SetSession(Session);
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
        rig.Lookup.SetSession(Session);

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
        rig.Lookup.SetSession(Session);

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
        rig.Lookup.SetSession(Session);

        rig.Lookup.Track(Enumerable.Range(0, MessageSendersRequest.MaxKeys + 1).Select(n => Old(new(EventNumber: n))).ToList());
        await WaitUntilAsync(() => rig.Source.Reads.Count == 2, what: "the second request");
        await rig.SettleAsync();

        await Assert.That(rig.Source.Reads.Select(r => r.Request.EventNumbers.Count)).IsEquivalentTo(new[] { MessageSendersRequest.MaxKeys, 1 });
    });
}
