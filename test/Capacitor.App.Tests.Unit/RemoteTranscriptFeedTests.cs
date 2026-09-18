using System.Globalization;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Remote.Models;
using Eventuous.SignalR;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.RemoteFixtures;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

/// The remote feed: one seed from the detail route, a tail from the seed's last event number,
/// resumption from the last position on re-establishment, and the two refusals.
public class RemoteTranscriptFeedTests {
    sealed class Harness : IDisposable {
        public readonly FakeServerLane Lane = new();
        public readonly BehaviorSubject<SessionAccessState> Access = new(SessionAccessState.Establishing);
        public readonly FakeTimeProvider Time = new();
        public readonly List<string> Logged = [];
        public SessionDetailFetch NextDetail = new(Detail(
            Event(0, CanonicalEventTypes.UserMessageReceived, Hello),
            Event(1, CanonicalEventTypes.AssistantTextGenerated, HiThere)));
        public int DetailReads;
        public readonly RemoteTranscriptFeed Feed;
        readonly string _sessionId = "s1";

        public Harness(string vendor = "gemini") =>
            Feed = new RemoteTranscriptFeed(_sessionId, vendor, Access, (_, _) => { DetailReads++; return Task.FromResult(NextDetail); }, Lane, Time, Logged.Add);

        public string Stream => StreamNames.AgentSession(_sessionId);
        public void Dispose() => Feed.Dispose();
    }

    [Test]
    public async Task Seeds_from_the_detail_route_then_tails_from_the_last_event_number() {
        using var h = new Harness();
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");
        await Assert.That(h.Lane.Tails[0]).IsEqualTo((h.Stream, (ulong?)1));

        var seed = h.Feed.ReadAppended();
        await Assert.That(seed.Status).IsEqualTo(FeedStatus.Reset);
        await Assert.That(seed.Lines.Select(l => l.Offset)).IsEquivalentTo(new long[] { 0, 1 });
        await Assert.That(seed.Lines[0].Projection.SubmittedInputs).IsEquivalentTo(new[] { "hello" });
        await Assert.That(seed.SnapshotOffset).IsEqualTo(2);
        await Assert.That(h.Feed.CurrentOffset).IsEqualTo(2);

        h.Lane.PushStreamEvent(Envelope("s1", 2, CanonicalEventTypes.AssistantToolCallsGenerated, LsCall));
        await WaitUntilAsync(() => h.Feed.CurrentOffset == 3, what: "the live event");
        var live = h.Feed.ReadAppended();
        await Assert.That(live.Status).IsEqualTo(FeedStatus.Ok);
        await Assert.That(live.Lines.Single().Offset).IsEqualTo(2);
        await Assert.That(live.Lines.Single().Projection.Envelopes[0].ToolCallId).IsEqualTo("t1");
        await Assert.That(h.DetailReads).IsEqualTo(1);
        await Assert.That(h.Feed.ReadAppended().Lines).IsEmpty();
    }

    /// The seed's boundary is where its replayed history ends. An event that beats the drain is
    /// not part of that history — folded into the Reset, it could no longer acknowledge a send.
    [Test]
    public async Task A_live_event_that_lands_before_the_seed_is_drained_follows_it_in_the_next_read() {
        using var h = new Harness();
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");
        h.Lane.PushStreamEvent(Envelope("s1", 2, CanonicalEventTypes.AssistantTextGenerated, """{"content":"more"}"""));
        await WaitUntilAsync(() => h.Feed.CurrentOffset == 3, what: "the live event");

        var seed = h.Feed.ReadAppended();
        await Assert.That(seed.Status).IsEqualTo(FeedStatus.Reset);
        await Assert.That(seed.Lines.Select(l => l.Offset)).IsEquivalentTo(new long[] { 0, 1 });
        await Assert.That(seed.SnapshotOffset).IsEqualTo(2);

        var live = h.Feed.ReadAppended();
        await Assert.That(live.Status).IsEqualTo(FeedStatus.Ok);
        await Assert.That(live.Lines.Single().Offset).IsEqualTo(2);
    }

    [Test]
    public async Task An_event_the_chat_does_not_render_moves_the_position_without_a_row() {
        using var h = new Harness();
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");
        h.Feed.ReadAppended();
        h.Lane.PushStreamEvent(Envelope("s1", 2, "InterruptIssued", """{"request_id":"r1"}"""));
        await WaitUntilAsync(() => h.Feed.CurrentOffset == 3, what: "the position");
        await Assert.That(h.Feed.ReadAppended().Lines).IsEmpty();
    }

    [Test]
    public async Task A_session_the_server_hides_reads_as_missing() {
        using var h = new Harness { NextDetail = new(null, NotFound: true) };
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Feed.PendingRunForTesting is { IsCompleted: true }, what: "the run");
        await Assert.That(h.Feed.ReadAppended().Status).IsEqualTo(FeedStatus.Missing);
        await Assert.That(h.Lane.Tails).IsEmpty();
    }

    [Test]
    public async Task A_denied_stream_reads_as_failed_and_is_not_retried() {
        using var h = new Harness();
        h.Lane.TailHandler = (_, _) => new HubException(WireTokens.StreamNotAuthorized);
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Feed.PendingRunForTesting is { IsCompleted: true }, what: "the run");
        h.Feed.ReadAppended(); // drains the seed the run committed before the tail was refused
        var read = h.Feed.ReadAppended();
        await Assert.That(read.Status).IsEqualTo(FeedStatus.Failed);
        await Assert.That(read.Failure).Contains("not authorized");
        await Assert.That(h.Feed.WaitingToRetryForTesting).IsFalse();
    }

    [Test]
    public async Task A_refused_stream_keeps_the_seed_rows_then_reports_the_failure() {
        using var h = new Harness();
        h.Lane.TailHandler = (_, _) => new HubException(WireTokens.StreamNotAuthorized);
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Feed.PendingRunForTesting is { IsCompleted: true }, what: "the run");

        var seed = h.Feed.ReadAppended();
        await Assert.That(seed.Status).IsEqualTo(FeedStatus.Reset);
        await Assert.That(seed.Lines.Count).IsEqualTo(2);
        await Assert.That(seed.SnapshotOffset).IsEqualTo(2);

        var failed = h.Feed.ReadAppended();
        await Assert.That(failed.Status).IsEqualTo(FeedStatus.Failed);
        await Assert.That(failed.Lines).IsEmpty();
        await Assert.That(failed.Failure).Contains("not authorized");
    }

    /// An unauthorized seed is a refusal like any other: without it the pane shows an empty
    /// transcript and nothing says the sign-in lapsed.
    [Test]
    public async Task An_unauthorized_seed_reads_as_failed_and_tails_nothing() {
        using var h = new Harness { NextDetail = new(null, Unauthorized: true) };
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Feed.PendingRunForTesting is { IsCompleted: true }, what: "the run");

        var read = h.Feed.ReadAppended();
        await Assert.That(read.Status).IsEqualTo(FeedStatus.Failed);
        await Assert.That(read.Failure).Contains("not signed in");
        await Assert.That(h.Lane.Tails).IsEmpty();
    }

    /// A hub that answers the subscribe with anything else — a method it does not have, say — is
    /// refusing too, and retrying it forever only hides that.
    [Test]
    public async Task A_stream_the_hub_refuses_for_any_other_reason_is_not_retried_either() {
        using var h = new Harness();
        h.Lane.TailHandler = (_, _) => new HubException("Method does not exist");
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Feed.PendingRunForTesting is { IsCompleted: true }, what: "the run");
        h.Feed.ReadAppended(); // drains the seed the run committed before the tail was refused

        var read = h.Feed.ReadAppended();
        await Assert.That(read.Status).IsEqualTo(FeedStatus.Failed);
        await Assert.That(read.Failure).Contains("Method does not exist");
        await Assert.That(h.Feed.WaitingToRetryForTesting).IsFalse();
        await Assert.That(h.Lane.Tails.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_re_established_access_resumes_the_tail_from_its_position_without_a_second_seed() {
        using var h = new Harness();
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the first tail");
        h.Feed.ReadAppended();
        h.Lane.PushStreamEvent(Envelope("s1", 2, CanonicalEventTypes.AssistantTextGenerated, """{"content":"more"}"""));
        await WaitUntilAsync(() => h.Feed.CurrentOffset == 3, what: "the live event");

        h.Access.OnNext(SessionAccessState.Unavailable);
        h.Access.OnNext(SessionAccessState.Establishing);
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 2, what: "the second tail");
        await Assert.That(h.Lane.Tails[1].From).IsEqualTo((ulong?)2);
        await Assert.That(h.DetailReads).IsEqualTo(1);
        await Assert.That(h.Feed.ReadAppended().Status).IsEqualTo(FeedStatus.Ok);
    }

    [Test]
    public async Task A_tail_that_ends_while_access_stands_resumes_after_the_retry_gap() {
        using var h = new Harness();
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the first tail");
        h.Lane.CloseTail(h.Stream);
        await WaitUntilAsync(() => h.Feed.WaitingToRetryForTesting, what: "the retry armed");
        h.Time.Advance(RemoteTranscriptFeed.Retry[0]);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 2, what: "the second tail");
        await Assert.That(h.Lane.Tails[1].From).IsEqualTo((ulong?)1);

        h.Lane.CloseTail(h.Stream);
        await WaitUntilAsync(() => h.Feed.WaitingToRetryForTesting, what: "the retry armed again");
        h.Time.Advance(RemoteTranscriptFeed.Retry[0]);
        await Assert.That(h.Lane.Tails.Count).IsEqualTo(2);
        h.Time.Advance(RemoteTranscriptFeed.Retry[1] - RemoteTranscriptFeed.Retry[0]);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 3, what: "the third tail");
    }

    [Test]
    public async Task A_lost_access_stops_the_tail_and_a_disposed_feed_tails_nothing_more() {
        using var h = new Harness();
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");
        h.Access.OnNext(SessionAccessState.Denied);
        await WaitUntilAsync(() => h.Feed.PendingRunForTesting is { IsCompleted: true }, what: "the run stopped");
        h.Feed.Dispose();
        h.Access.OnNext(SessionAccessState.Established);
        await Task.Delay(50);
        await Assert.That(h.Lane.Tails.Count).IsEqualTo(1);
    }

    /// A refusal that lands after the run it belongs to was stopped is that run's verdict, not
    /// the current one's.
    [Test]
    public async Task A_refusal_from_a_superseded_run_is_not_reported() {
        using var h = new Harness();
        var gate = new TaskCompletionSource();
        h.Lane.TailHandler = (_, _) => {
            gate.Task.Wait(TimeSpan.FromSeconds(5));
            return new HubException(WireTokens.StreamNotAuthorized);
        };
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");

        h.Access.OnNext(SessionAccessState.Unavailable);
        gate.SetResult();
        await WaitUntilAsync(() => h.Feed.PendingRunForTesting is { IsCompleted: true }, what: "the run");

        await Assert.That(h.Feed.ReadAppended().Status).IsEqualTo(FeedStatus.Reset);
        await Assert.That(h.Feed.ReadAppended().Status).IsEqualTo(FeedStatus.Ok);
    }

    /// The pane polls, so a verdict answered once would be gone by the next poll; it stands until
    /// a new attempt seeds, and a verdict the previous attempt left behind never outlives that.
    [Test]
    public async Task A_verdict_answers_every_read_until_a_new_attempt_seeds() {
        using var h = new Harness { NextDetail = new(null, Unauthorized: true) };
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Feed.PendingRunForTesting is { IsCompleted: true }, what: "the run");
        await Assert.That(h.Feed.ReadAppended().Status).IsEqualTo(FeedStatus.Failed);
        await Assert.That(h.Feed.ReadAppended().Status).IsEqualTo(FeedStatus.Failed);

        h.NextDetail = new(Detail(Event(0, CanonicalEventTypes.UserMessageReceived, Hello)));
        h.Access.OnNext(SessionAccessState.Establishing);
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");

        var seed = h.Feed.ReadAppended();
        await Assert.That(seed.Status).IsEqualTo(FeedStatus.Reset);
        await Assert.That(seed.Lines.Count).IsEqualTo(1);
        await Assert.That(h.Feed.ReadAppended().Status).IsEqualTo(FeedStatus.Ok);
    }

    /// A notification marked meta yields no row and no input, only its finish; the feed must keep it.
    [Test]
    public async Task A_signal_only_projection_survives_the_feed() {
        using var h = new Harness(vendor: "claude");
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");
        h.Feed.ReadAppended();
        h.Lane.PushStreamEvent(Envelope("s1", 2, CanonicalEventTypes.UserMessageReceived,
            """{"content":"<task-notification>\n<task-id>a9f262478e032f427</task-id>\n<tool-use-id>toolu_A</tool-use-id>\n<status>completed</status>\n<summary>done</summary>\n</task-notification>","extensions":{"claude_code":{"is_meta":true}}}"""));
        await WaitUntilAsync(() => h.Feed.CurrentOffset == 3, what: "the position");

        var line = h.Feed.ReadAppended().Lines.Single();
        await Assert.That(line.Offset).IsEqualTo(2);
        await Assert.That(line.Projection.Envelopes).IsEmpty();
        await Assert.That(line.Projection.SubmittedInputs).IsEmpty();
        var finished = (SubagentSignal.Finished)line.Projection.Subagents.Single();
        await Assert.That(finished.CallId).IsEqualTo("toolu_A");
        await Assert.That(finished.AgentId).IsEqualTo("a9f262478e032f427");
        await Assert.That(finished.Outcome).IsEqualTo(SubagentOutcome.Done);
    }

    /// The payload's own time is transcript-authoritative; the stream envelope only carries when
    /// the server happened to store it.
    [Test]
    public async Task A_live_event_dated_by_its_own_payload_keeps_that_time_not_the_storage_time() {
        using var h = new Harness();
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");
        h.Feed.ReadAppended();

        h.Lane.PushStreamEvent(Envelope("s1", 2, CanonicalEventTypes.UserMessageReceived,
            """{"content":"hello","timestamp":"2026-09-17T10:01:00Z"}"""));
        await WaitUntilAsync(() => h.Feed.CurrentOffset == 3, what: "the live event");

        var envelope = h.Feed.ReadAppended().Lines.Single().Projection.Envelopes.Single();
        var at = DateTimeOffset.Parse(envelope.TimestampIso!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        await Assert.That(at).IsEqualTo(DateTimeOffset.Parse("2026-09-17T10:01:00Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    [Test]
    public async Task A_live_event_with_no_payload_timestamp_keeps_the_envelope_storage_time() {
        using var h = new Harness();
        h.Access.OnNext(SessionAccessState.Established);
        await WaitUntilAsync(() => h.Lane.Tails.Count == 1, what: "the tail");
        h.Feed.ReadAppended();

        var storedAt = new DateTime(2026, 9, 17, 9, 0, 0, DateTimeKind.Utc);
        h.Lane.PushStreamEvent(new StreamEventEnvelope {
            EventId = Guid.NewGuid(), Stream = h.Stream, EventType = CanonicalEventTypes.UserMessageReceived,
            StreamPosition = 2, GlobalPosition = 2, Timestamp = storedAt, JsonPayload = """{"content":"hello"}""",
        });
        await WaitUntilAsync(() => h.Feed.CurrentOffset == 3, what: "the live event");

        var envelope = h.Feed.ReadAppended().Lines.Single().Projection.Envelopes.Single();
        var at = DateTimeOffset.Parse(envelope.TimestampIso!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        await Assert.That(at).IsEqualTo((DateTimeOffset)storedAt);
    }
}
