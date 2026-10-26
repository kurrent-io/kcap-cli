using ReactiveUnit = System.Reactive.Unit;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.MessageSenders;
using Capacitor.Remote.Models;
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
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel("AvaloniaSession")]
    public Task Server_attribution_confirms_the_retry_without_confusing_its_predecessor(bool oldEchoArrivesFirst) => RunOnUiAsync(async () => {
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var queue = new Subject<IReadOnlyList<QueuedInputItem>>();
        var time = new FakeTimeProvider();
        var lane = new FakeServerLane();
        var input = new AcceptingChatPromptInput();
        var originalId = Guid.NewGuid();
        var retryId = Guid.NewGuid();
        var receipts = new List<MessageSenderDto>();
        var senders = new FakeMessageSenderSource { Answer = _ => new(MessageSendersReadKind.Ready, receipts.ToArray()) };
        var feed = new RemoteTranscriptFeed("s1", "gemini", access,
            (_, _) => Task.FromResult(new SessionDetailFetch(RemoteFixtures.Detail())), lane, time, _ => { });
        var chat = new ChatTabViewModel("a1", AgentOrigin.Remote, Observable.Return(Session("s1")), Observable.Return<string[]?>(null),
            input, new NoAttachmentUploader(), _ => feed, new RecordingOpener(), time,
            new FakePermissionService(), new SessionRuns(time), sessionId: Observable.Return<string?>("s1"),
            serverQueue: queue, senders: senders);
        try {
            await WaitUntilAsync(() => chat.HasSeed && lane.Tails.Count == 1, what: "the seed and live tail");
            chat.ComposerText = "same prompt";
            await chat.SendCommand.Execute();
            var original = chat.QueuedMessages.Single();
            queue.OnNext([new() { DispatchId = originalId, Text = "same prompt", SenderUserId = "u1" }]);
            queue.OnNext([]);
            chat.ComposerText = "unrelated draft";
            await chat.SendQueuedCommand.Execute(original);
            var retry = chat.QueuedMessages.Single();
            queue.OnNext([new() { DispatchId = retryId, Text = "same prompt", SenderUserId = "u1" }]);

            if (oldEchoArrivesFirst) {
                receipts.Add(new() { EventNumber = 4, DispatchId = originalId, SenderUserId = "u1", SentByCaller = true });
                lane.PushStreamEvent(RemoteFixtures.Envelope("s1", 4, CanonicalEventTypes.UserMessageReceived, """{"content":"same prompt"}"""));
                await WaitUntilAsync(() => chat.Items.OfType<UserTurnItem>().Count() == 1, what: "the predecessor echo");
                if (chat.PendingSenderReadForTesting is { } oldRead) await oldRead;
                await Assert.That(chat.QueuedMessages.Single()).IsSameReferenceAs(retry);
            }

            receipts.Add(new() { EventNumber = 6, DispatchId = retryId, SenderUserId = "u1", SentByCaller = true });
            lane.PushStreamEvent(RemoteFixtures.Envelope("s1", 6, CanonicalEventTypes.UserMessageReceived, """{"content":"same prompt"}"""));
            await WaitUntilAsync(() => chat.Items.OfType<UserTurnItem>().LastOrDefault()?.Sender.HasTitle == true
                && chat.Items.OfType<UserTurnItem>().Count() == (oldEchoArrivesFirst ? 2 : 1), what: "the retry echo and attribution");
            if (chat.PendingSenderReadForTesting is { } read) await read;
            queue.OnNext([]);

            await Assert.That(chat.HasQueuedMessages).IsFalse();
            await Assert.That(chat.ComposerText).IsEqualTo("unrelated draft");
            await Assert.That(input.Sends).IsEqualTo(2);
        } finally { await chat.TeardownAsync(); }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_late_queue_snapshot_cannot_revive_a_dispatch_already_attributed_to_a_message() => RunOnUiAsync(async () => {
        using var access = new BehaviorSubject<SessionAccessState>(SessionAccessState.Established);
        using var queue = new Subject<IReadOnlyList<QueuedInputItem>>();
        var time = new FakeTimeProvider();
        var lane = new FakeServerLane();
        var dispatch = Guid.NewGuid();
        var senders = new FakeMessageSenderSource {
            Answer = _ => new(MessageSendersReadKind.Ready, [new() { EventNumber = 6, DispatchId = dispatch, SentByCaller = true }]),
        };
        var feed = new RemoteTranscriptFeed("s1", "gemini", access,
            (_, _) => Task.FromResult(new SessionDetailFetch(RemoteFixtures.Detail())), lane, time, _ => { });
        var chat = new ChatTabViewModel("a1", AgentOrigin.Remote, Observable.Return(Session("s1")), Observable.Return<string[]?>(null),
            new AcceptingChatPromptInput(), new NoAttachmentUploader(), _ => feed, new RecordingOpener(), time,
            new FakePermissionService(), new SessionRuns(time), sessionId: Observable.Return<string?>("s1"),
            serverQueue: queue, senders: senders);
        try {
            await WaitUntilAsync(() => chat.HasSeed && lane.Tails.Count == 1, what: "the seed and tail");
            chat.ComposerText = "same prompt";
            await chat.SendCommand.Execute();
            lane.PushStreamEvent(RemoteFixtures.Envelope("s1", 6, CanonicalEventTypes.UserMessageReceived, """{"content":"same prompt"}"""));
            await WaitUntilAsync(() => chat.Items.OfType<UserTurnItem>().SingleOrDefault()?.Sender.HasTitle == true, what: "the echo and attribution");
            await Assert.That(chat.HasQueuedMessages).IsFalse();

            queue.OnNext([new() { DispatchId = dispatch, Text = "same prompt", SenderUserId = "u1" }]);

            await Assert.That(chat.HasQueuedMessages).IsFalse();
        } finally { await chat.TeardownAsync(); }
    });

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

    /// The feed is not replayed when the session or the viewer changes, so the chat hands its visible messages over itself.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_session_change_and_a_sign_in_ask_again_about_the_messages_on_screen() => RunOnUiAsync(async () => {
        var time = new FakeTimeProvider();
        var id = Guid.NewGuid();
        using var sessionId = new BehaviorSubject<string?>("s1");
        using var signedIn = new Subject<ReactiveUnit>();
        var senders = new FakeMessageSenderSource();
        senders.Answer = _ => new(MessageSendersReadKind.Ready, [new MessageSenderDto { EventId = id, SenderName = $"read {senders.Reads.Count}" }]);
        var feed = new LinesFeed(UserLine("hello", 0, id));
        var chat = new ChatTabViewModel("a1", AgentOrigin.Local, Observable.Return(Session("/t.jsonl")), Observable.Return<string[]?>(null),
            new ReadOnlyChatInput(), new NoAttachmentUploader(), _ => feed, new RecordingOpener(), time,
            new FakePermissionService(), new SessionRuns(time), sessionId: sessionId, senders: senders, signInCompleted: signedIn);
        try {
            await WaitUntilAsync(() => chat.Items.OfType<UserTurnItem>().SingleOrDefault()?.Sender.Title == "read 1", what: "the first title");
            var turn = chat.Items.OfType<UserTurnItem>().Single();

            sessionId.OnNext("s2");
            await WaitUntilAsync(() => turn.Sender.Title == "read 2", what: "the title under the new session");
            await Assert.That(senders.Reads[1].SessionId).IsEqualTo("s2");

            signedIn.OnNext(ReactiveUnit.Default);
            await WaitUntilAsync(() => turn.Sender.Title == "read 3", what: "the title after the sign-in");
        } finally { await chat.TeardownAsync(); }
    });
}
