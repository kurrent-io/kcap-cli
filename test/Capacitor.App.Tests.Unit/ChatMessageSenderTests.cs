using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.MessageSenders;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

/// Which keys the chat looks a user message up by, per feed, and the title the answer gives it.
public class ChatMessageSenderTests {
    static ChatSessionInfo Session(string feedKey) => new("Running", "Running", "gemini", null, null, null, false, "", feedKey);

    sealed class LinesFeed(params ProjectedLine[] lines) : IChatTranscriptFeed {
        bool _read;
        public long? CurrentOffset => 0;
        public FeedRead ReadAppended() {
            if (_read) return new(FeedStatus.Ok, []);
            _read = true;
            return new(FeedStatus.Ok, lines);
        }
        public void Dispose() { }
    }

    static ProjectedLine UserLine(string text, long offset, Guid? sourceEventId = null, Guid? dispatchId = null) => new(
        new ChatProjectionResult([new AcpEventEnvelope(Kind: AcpEventKind.UserMessage, Text: text, SourceEventId: sourceEventId, DispatchId: dispatchId)], [text], []), offset);

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_remote_message_is_looked_up_by_its_event_number_in_the_session() => RunOnUiAsync(async () => {
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        var time = new FakeTimeProvider();
        var lane = new FakeServerLane();
        var senders = new FakeMessageSenderSource {
            Answer = _ => new(MessageSendersReadKind.Ready, [
                new MessageSenderDto { EventNumber = 4, SenderName = "Tony" },
                new MessageSenderDto { EventNumber = 6, SentByCaller = true },
            ]),
        };
        var feed = new RemoteTranscriptFeed("s1", "gemini", access,
            (_, _) => Task.FromResult(new SessionDetailFetch(RemoteFixtures.Detail(
                RemoteFixtures.Event(4, CanonicalEventTypes.UserMessageReceived, RemoteFixtures.Hello)))), lane, time, _ => { });
        var chat = new ChatTabViewModel("a1", AgentOrigin.Remote, Observable.Return(Session("s1")), Observable.Return<string[]?>(null),
            new ReadOnlyChatInput(), new NoAttachmentUploader(), _ => feed, new RecordingOpener(), time,
            new FakePermissionService(), new SessionRuns(time), sessionId: Observable.Return<string?>("s1"), senders: senders);
        try {
            await WaitUntilAsync(() => chat.Items.Count == 1 && lane.Tails.Count == 1, what: "the seeded message and the tail");
            lane.PushStreamEvent(RemoteFixtures.Envelope("s1", 6, CanonicalEventTypes.UserMessageReceived, """{"content":"again"}"""));
            await WaitUntilAsync(() => chat.Items.Count == 2, what: "the live message");
            var turns = chat.Items.OfType<UserTurnItem>().ToList();
            await WaitUntilAsync(() => turns.All(t => t.Sender.HasTitle), what: "both senders named");

            await Assert.That(turns.Select(t => t.Sender.Keys)).IsEquivalentTo(new[] { new MessageKeys(EventNumber: 4), new MessageKeys(EventNumber: 6) });
            await Assert.That(turns.Select(t => t.Sender.Title)).IsEquivalentTo(new string?[] { "Tony", "You" });
            await Assert.That(senders.Reads.All(r => r.SessionId == "s1" && r.Request.EventIds.Count == 0)).IsTrue();
        } finally { await chat.TeardownAsync(); }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_local_transcript_message_is_looked_up_by_its_stored_event_id_and_an_unnamed_one_keeps_the_fallback() => RunOnUiAsync(async () => {
        var time = new FakeTimeProvider();
        Guid named = Guid.NewGuid(), unnamed = Guid.NewGuid();
        var senders = new FakeMessageSenderSource {
            Answer = _ => new(MessageSendersReadKind.Ready, [new MessageSenderDto { EventId = named, EventNumber = 40, SenderName = "Tony" }]),
        };
        var feed = new LinesFeed(UserLine("from Tony", 40, named), UserLine("typed here", 900, unnamed));
        var chat = new ChatTabViewModel("a1", AgentOrigin.Local, Observable.Return(Session("/t.jsonl")), Observable.Return<string[]?>(null),
            new ReadOnlyChatInput(), new NoAttachmentUploader(), _ => feed, new RecordingOpener(), time,
            new FakePermissionService(), new SessionRuns(time), sessionId: Observable.Return<string?>("s1"), senders: senders);
        try {
            await WaitUntilAsync(() => chat.Items.Count == 2 && senders.Reads.Count > 0, what: "both messages asked about");
            if (chat.PendingSenderReadForTesting is { } read) await read;
            var turns = chat.Items.OfType<UserTurnItem>().ToList();

            await Assert.That(turns.Select(t => t.Sender.Keys)).IsEquivalentTo(new[] { new MessageKeys(named), new MessageKeys(unnamed) });
            await Assert.That(turns[0].Sender.Title).IsEqualTo("Tony");
            await Assert.That(turns[1].Sender.HasTitle).IsFalse();
            await Assert.That(chat.UserTitle).IsEqualTo("You");
            await Assert.That(senders.Reads[0].Request.EventNumbers).IsEmpty();
        } finally { await chat.TeardownAsync(); }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_journaled_message_is_looked_up_by_its_dispatch_and_one_with_none_is_never_asked_about() => RunOnUiAsync(async () => {
        var time = new FakeTimeProvider();
        var dispatch = Guid.NewGuid();
        var senders = new FakeMessageSenderSource {
            Answer = _ => new(MessageSendersReadKind.Ready, [new MessageSenderDto { DispatchId = dispatch, SenderName = "Tony" }]),
        };
        var feed = new LinesFeed(UserLine("dispatched", 0, dispatchId: dispatch), UserLine("typed here", 0));
        var chat = new ChatTabViewModel("a1", AgentOrigin.Local, Observable.Return(Session("/journal.jsonl")), Observable.Return<string[]?>(null),
            new ReadOnlyChatInput(), new NoAttachmentUploader(), _ => feed, new RecordingOpener(), time,
            new FakePermissionService(), new SessionRuns(time), sessionId: Observable.Return<string?>("s1"), senders: senders);
        try {
            await WaitUntilAsync(() => chat.Items.Count == 2 && senders.Reads.Count > 0, what: "the dispatched message asked about");
            if (chat.PendingSenderReadForTesting is { } read) await read;
            var turns = chat.Items.OfType<UserTurnItem>().ToList();

            await Assert.That(turns.Select(t => t.Sender.Keys)).IsEquivalentTo(new[] { new MessageKeys(DispatchId: dispatch), new MessageKeys() });
            await Assert.That(turns[0].Sender.Title).IsEqualTo("Tony");
            await Assert.That(turns[1].Sender.HasTitle).IsFalse();
            await Assert.That(senders.Reads.Single().Request.DispatchIds).IsEquivalentTo(new[] { dispatch });
            await Assert.That(senders.Reads.Single().Request.EventIds).IsEmpty();
        } finally { await chat.TeardownAsync(); }
    });
}
