using System.Text;
using Capacitor.Cli.Harness.MuseAssistant;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Tests.Unit.Harness.MuseAssistant;

/// <summary>Pins how a chat is cut into sessions: boundaries are a pure function of selected items'
/// timestamps, ignored items only move the watermark, idle closure is judged at the fetch time with a
/// 30-minute margin, and every action carries the state to write before and after its post.</summary>
public class MuseAssistantPlannerTests {
    const string User  = "user_01HXYZ";
    const string Chat  = "chat-main";
    const string Grant = "0123456789abcdef0123456789abcdef";
    const string Home  = "/home/hatch";
    const long Minute = 60_000, Hour = 3_600_000, T0 = 1_760_000_000_000;

    static MuseAssistantChatState Fresh(string boundary = MuseAssistantGrantBoundaries.History, long? afterSeq = null) =>
        new(Chat, "https://acme.kcap.ai", User, new(Grant, boundary, afterSeq, T0 - Hour, null), null, null, null);

    static MuseAssistantItem Item(long seq, long at, string role = "user", string turn = "t1") =>
        new(turn, role, seq, at, $"m{seq}", $$"""{"chat_id":"chat-main","turn_id":"{{turn}}","role":"{{role}}","seq":{{seq}},"occurred_at_ms":{{at}},"content":"m{{seq}}"}""");

    static MuseAssistantWalk Walk(long fetchedAt, params MuseAssistantItem[] items) =>
        new(Chat, true, fetchedAt, items.OrderBy(i => i.Seq).ToList(), OldestHasMore: false, OldestPageMinSeq: items.Length == 0 ? null : items.Min(i => i.Seq));

    static IReadOnlyList<MuseAssistantAction> Plan(MuseAssistantChatState state, MuseAssistantWalk walk) => MuseAssistantPlanner.Plan(state, walk, User, Home);

    static string Id(long firstSeq, string grant = Grant) => MuseAssistantSessionId.For(User, Chat, grant, firstSeq);

    static IEnumerable<string> Kinds(IEnumerable<MuseAssistantAction> actions) => actions.Select(a => a.GetType().Name);

    [Test]
    public async Task First_selected_items_open_a_session_and_send_them_in_seq_order() {
        var actions = Plan(Fresh(), Walk(T0 + 20_000, Item(15, T0 + 12_000, "assistant"), Item(12, T0 + 5_000), Item(13, T0 + 7_000, "commentary")));

        await Assert.That(Kinds(actions)).IsEquivalentTo([nameof(MuseAssistantStartSession), nameof(MuseAssistantSendLines)], CollectionOrdering.Matching);
        var start = (MuseAssistantStartSession)actions[0];
        var send  = (MuseAssistantSendLines)actions[1];
        await Assert.That(start.Session.SessionId).IsEqualTo(Id(12));
        await Assert.That(start.Session).IsEqualTo(new MuseAssistantOpenSession(Id(12), 12, T0 + 5_000, true, Home, MuseAssistantSessionPhases.PendingStart, null));
        await Assert.That(start.Intent!.Open!.Phase).IsEqualTo(MuseAssistantSessionPhases.PendingStart);
        await Assert.That(start.After.Open!.Phase).IsEqualTo(MuseAssistantSessionPhases.Started);
        await Assert.That(start.After.Watermark).IsNull();
        await Assert.That(send.SessionId).IsEqualTo(Id(12));
        await Assert.That(send.Seqs).IsEquivalentTo([12, 13, 15], CollectionOrdering.Matching);
        await Assert.That(send.After.Watermark).IsEqualTo(15);
        await Assert.That(send.After.LastSelectedAtMs).IsEqualTo(T0 + 12_000);
    }

    [Test]
    public async Task A_leading_developer_item_neither_opens_nor_names_the_session_but_moves_the_watermark() {
        var actions = Plan(Fresh(), Walk(T0 + Minute, Item(10, T0, "developer"), Item(12, T0 + 5_000)));

        var start = (MuseAssistantStartSession)actions[0];
        await Assert.That(start.Session.FirstSeq).IsEqualTo(12);
        await Assert.That(start.Session.StartedAtMs).IsEqualTo(T0 + 5_000);
        await Assert.That(((MuseAssistantSendLines)actions[1]).Seqs).IsEquivalentTo([12]);
        await Assert.That(actions[^1].After.Watermark).IsEqualTo(12);
    }

    [Test]
    public async Task A_developer_only_walk_advances_the_watermark_and_posts_nothing() {
        var actions = Plan(Fresh(), Walk(T0 + Minute, Item(10, T0, "developer"), Item(11, T0 + 1, "developer")));

        await Assert.That(Kinds(actions)).IsEquivalentTo([nameof(MuseAssistantAdvanceWatermark)]);
        await Assert.That(actions[0].After.Watermark).IsEqualTo(11);
        await Assert.That(actions[0].After.Open).IsNull();
        await Assert.That(actions[0].After.LastSelectedAtMs).IsNull();
    }

    [Test]
    public async Task A_two_hour_gap_between_selected_items_splits_sessions_while_developer_items_straddling_it_do_nothing() {
        var actions = Plan(Fresh(), Walk(T0 + 3 * Hour + Minute,
            Item(1, T0), Item(2, T0 + Hour, "developer"), Item(3, T0 + 2 * Hour + Minute, "developer"), Item(4, T0 + 3 * Hour)));

        await Assert.That(Kinds(actions)).IsEquivalentTo([
            nameof(MuseAssistantStartSession), nameof(MuseAssistantSendLines), nameof(MuseAssistantEndSession),
            nameof(MuseAssistantStartSession), nameof(MuseAssistantSendLines)], CollectionOrdering.Matching);
        var end = (MuseAssistantEndSession)actions[2];
        await Assert.That(end.Session.SessionId).IsEqualTo(Id(1));
        await Assert.That(end.EndedAtMs).IsEqualTo(T0);
        await Assert.That(end.Intent!.Open!.Phase).IsEqualTo(MuseAssistantSessionPhases.PendingEnd);
        await Assert.That(end.Intent.Open.EndedAtMs).IsEqualTo(T0);
        await Assert.That(end.After.Open).IsNull();
        await Assert.That(((MuseAssistantStartSession)actions[3]).Session.SessionId).IsEqualTo(Id(4));
        await Assert.That(actions[^1].After.Watermark).IsEqualTo(4);
    }

    [Test]
    public async Task Items_inside_the_gap_stay_in_one_session_and_the_gap_is_inclusive() {
        var inside = Plan(Fresh(), Walk(T0 + 3 * Hour, Item(1, T0), Item(2, T0 + 2 * Hour - 1)));
        var exact  = Plan(Fresh(), Walk(T0 + 3 * Hour, Item(1, T0), Item(2, T0 + 2 * Hour)));

        await Assert.That(inside.OfType<MuseAssistantStartSession>().Count()).IsEqualTo(1);
        await Assert.That(exact.OfType<MuseAssistantStartSession>().Count()).IsEqualTo(2);
    }

    [Test]
    public async Task An_empty_run_closes_an_idle_session_only_once_the_fetch_time_is_two_and_a_half_hours_past_the_last_item() {
        var open = new MuseAssistantOpenSession(Id(1), 1, T0, true, Home, MuseAssistantSessionPhases.Started, null);
        var state = Fresh() with { Watermark = 1, LastSelectedAtMs = T0, Open = open };

        var tooEarly = Plan(state, Walk(T0 + 2 * Hour + 30 * Minute - 1));
        var closes   = Plan(state, Walk(T0 + 2 * Hour + 30 * Minute));

        await Assert.That(tooEarly).IsEmpty();
        var end = (MuseAssistantEndSession)closes.Single();
        await Assert.That(end.EndedAtMs).IsEqualTo(T0);
        await Assert.That(end.Intent!.Open!.Phase).IsEqualTo(MuseAssistantSessionPhases.PendingEnd);
        await Assert.That(end.After.Open).IsNull();
        await Assert.That(end.After.Watermark).IsEqualTo(1);
    }

    [Test]
    public async Task Idle_closure_tolerates_a_vm_clock_under_thirty_minutes_ahead_and_splits_beyond_it() {
        // Store clock: last item 10:00, walk fetched 11:50 with nothing newer, then an item at 11:59.
        var tenOClock = T0;
        var state = Fresh() with { Watermark = 1, LastSelectedAtMs = tenOClock,
            Open = new MuseAssistantOpenSession(Id(1), 1, tenOClock, true, Home, MuseAssistantSessionPhases.Started, null) };

        var vmTwentyAhead    = Plan(state, Walk(tenOClock + 110 * Minute + 20 * Minute));
        var vmFortyFiveAhead = Plan(state, Walk(tenOClock + 110 * Minute + 45 * Minute));

        await Assert.That(vmTwentyAhead).IsEmpty();
        await Assert.That(vmFortyFiveAhead.Single()).IsTypeOf<MuseAssistantEndSession>();

        var continued  = Plan(state, Walk(tenOClock + 125 * Minute, Item(2, tenOClock + 119 * Minute)));
        var afterSplit = Plan(vmFortyFiveAhead[0].After, Walk(tenOClock + 125 * Minute, Item(2, tenOClock + 119 * Minute)));

        await Assert.That(Kinds(continued)).IsEquivalentTo([nameof(MuseAssistantSendLines)]);
        await Assert.That(((MuseAssistantSendLines)continued[0]).SessionId).IsEqualTo(Id(1));
        await Assert.That(Kinds(afterSplit)).IsEquivalentTo([nameof(MuseAssistantStartSession), nameof(MuseAssistantSendLines)], CollectionOrdering.Matching);
        await Assert.That(((MuseAssistantStartSession)afterSplit[0]).Session.SessionId).IsEqualTo(Id(2));
    }

    [Test]
    public async Task Items_at_or_below_the_watermark_are_skipped() {
        var state = Fresh() with { Watermark = 15, LastSelectedAtMs = T0 + 12_000,
            Open = new MuseAssistantOpenSession(Id(12), 12, T0 + 5_000, true, Home, MuseAssistantSessionPhases.Started, null) };

        var actions = Plan(state, Walk(T0 + Minute, Item(12, T0 + 5_000), Item(15, T0 + 12_000, "assistant"), Item(22, T0 + 20_000)));

        var send = (MuseAssistantSendLines)actions.Single();
        await Assert.That(send.Seqs).IsEquivalentTo([22]);
        await Assert.That(send.SessionId).IsEqualTo(Id(12));
    }

    [Test]
    public async Task Is_main_rides_the_start_and_its_replay() {
        var walk = Walk(T0 + Minute, Item(1, T0)) with { IsMain = false };
        var start = (MuseAssistantStartSession)Plan(Fresh(), walk)[0];

        await Assert.That(start.Session.IsMain).IsFalse();

        var replay = (MuseAssistantStartSession)Plan(start.Intent!, Walk(T0 + Minute))[0];
        await Assert.That(replay.Session).IsEqualTo(start.Session);
        await Assert.That(replay.Intent).IsNull();
        await Assert.That(replay.After.Open!.Phase).IsEqualTo(MuseAssistantSessionPhases.Started);
    }

    [Test]
    public async Task A_pending_start_is_replayed_before_new_items_and_a_pending_end_is_completed_first() {
        var pendingStart = Fresh() with { Open = new MuseAssistantOpenSession(Id(1), 1, T0, true, Home, MuseAssistantSessionPhases.PendingStart, null) };
        var resumed = Plan(pendingStart, Walk(T0 + Minute, Item(1, T0), Item(2, T0 + 1)));

        await Assert.That(Kinds(resumed)).IsEquivalentTo([nameof(MuseAssistantStartSession), nameof(MuseAssistantSendLines)], CollectionOrdering.Matching);
        await Assert.That(((MuseAssistantSendLines)resumed[1]).Seqs).IsEquivalentTo([1, 2], CollectionOrdering.Matching);

        var pendingEnd = Fresh() with { Watermark = 1, LastSelectedAtMs = T0,
            Open = new MuseAssistantOpenSession(Id(1), 1, T0, true, Home, MuseAssistantSessionPhases.PendingEnd, T0) };
        var completed = Plan(pendingEnd, Walk(T0 + 3 * Hour, Item(5, T0 + 3 * Hour)));

        await Assert.That(Kinds(completed)).IsEquivalentTo(
            [nameof(MuseAssistantEndSession), nameof(MuseAssistantStartSession), nameof(MuseAssistantSendLines)], CollectionOrdering.Matching);
        await Assert.That(((MuseAssistantEndSession)completed[0]).EndedAtMs).IsEqualTo(T0);
        await Assert.That(completed[0].Intent).IsNull();
    }

    /// <summary>A crash after a new session's start (or its first batch) was accepted leaves the previous
    /// session's last-selected time in the state. Gap-checking against it would end the new session before
    /// it began and post its start a second time.</summary>
    [Test]
    [Arguments(MuseAssistantSessionPhases.PendingStart)]
    [Arguments(MuseAssistantSessionPhases.Started)]
    public async Task A_new_session_with_the_previous_sessions_last_selected_time_continues_with_its_first_item(string phase) {
        var second = new MuseAssistantOpenSession(Id(5), 5, T0 + 3 * Hour, true, Home, phase, null);
        var state  = Fresh() with { Watermark = 4, LastSelectedAtMs = T0, Open = second };

        var actions = Plan(state, Walk(T0 + 3 * Hour + Minute, Item(4, T0), Item(5, T0 + 3 * Hour), Item(6, T0 + 3 * Hour + 1)));

        await Assert.That(actions.OfType<MuseAssistantEndSession>()).IsEmpty();
        await Assert.That(actions.OfType<MuseAssistantStartSession>().Count()).IsEqualTo(phase == MuseAssistantSessionPhases.PendingStart ? 1 : 0);
        var send = actions.OfType<MuseAssistantSendLines>().Single();
        await Assert.That(send.SessionId).IsEqualTo(Id(5));
        await Assert.That(send.Seqs).IsEquivalentTo([5, 6], CollectionOrdering.Matching);
        await Assert.That(send.After.LastSelectedAtMs).IsEqualTo(T0 + 3 * Hour + 1);
    }

    [Test]
    public async Task A_new_session_with_the_previous_sessions_last_selected_time_idles_and_ends_from_its_own_start() {
        var second = new MuseAssistantOpenSession(Id(5), 5, T0 + 3 * Hour, true, Home, MuseAssistantSessionPhases.Started, null);
        var state  = Fresh() with { Watermark = 4, LastSelectedAtMs = T0, Open = second };

        await Assert.That(Plan(state, Walk(T0 + 3 * Hour + Minute))).IsEmpty();
        await Assert.That(((MuseAssistantEndSession)Plan(state, Walk(T0 + 5 * Hour + 30 * Minute)).Single()).EndedAtMs).IsEqualTo(T0 + 3 * Hour);
        await Assert.That(((MuseAssistantEndSession)MuseAssistantPlanner.Closure(state).Single()).EndedAtMs).IsEqualTo(T0 + 3 * Hour);
        await Assert.That(MuseAssistantPlanner.SessionEndedAtMs(state, second)).IsEqualTo(T0 + 3 * Hour);
    }

    [Test]
    public async Task Items_at_or_below_after_seq_are_ignored_like_developer_items() {
        var actions = Plan(Fresh(MuseAssistantGrantBoundaries.AfterSeq, afterSeq: 12), Walk(T0 + Minute, Item(11, T0), Item(12, T0 + 1), Item(13, T0 + 2)));

        var start = (MuseAssistantStartSession)actions[0];
        await Assert.That(start.Session.FirstSeq).IsEqualTo(13);
        await Assert.That(((MuseAssistantSendLines)actions[1]).Seqs).IsEquivalentTo([13]);
        await Assert.That(actions[1].After.Watermark).IsEqualTo(13);

        var onlyBelow = Plan(Fresh(MuseAssistantGrantBoundaries.AfterSeq, afterSeq: 12), Walk(T0 + Minute, Item(11, T0), Item(12, T0 + 1)));
        await Assert.That(onlyBelow.Single()).IsTypeOf<MuseAssistantAdvanceWatermark>();
        await Assert.That(onlyBelow[0].After.Watermark).IsEqualTo(12);
    }

    [Test]
    public async Task Batches_cap_at_one_hundred_lines_and_each_advances_the_watermark() {
        var items = Enumerable.Range(1, 105).Select(i => Item(i, T0 + i)).ToArray();

        var actions = Plan(Fresh(), Walk(T0 + Minute, items));

        var sends = actions.OfType<MuseAssistantSendLines>().ToList();
        await Assert.That(sends.Count).IsEqualTo(2);
        await Assert.That(sends[0].Seqs.Length).IsEqualTo(100);
        await Assert.That(sends[0].After.Watermark).IsEqualTo(100);
        await Assert.That(sends[1].Seqs).IsEquivalentTo([101, 102, 103, 104, 105], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Batches_cap_on_the_escaped_size_each_line_has_in_the_request() {
        // 'é' is 2 UTF-8 bytes raw but 6 bytes as the \u00e9 escape the serializer writes.
        static MuseAssistantItem Wide(long seq) => Item(seq, T0 + seq) with { Line = new string('é', 500_000) };

        var actions = Plan(Fresh(), Walk(T0 + Minute, Wide(1), Wide(2)));

        var sends = actions.OfType<MuseAssistantSendLines>().ToList();
        await Assert.That(sends.Select(s => s.Seqs.Length)).IsEquivalentTo([1, 1], CollectionOrdering.Matching);
        await Assert.That(sends[0].After.Watermark).IsEqualTo(1);
    }

    [Test]
    public async Task Every_batch_request_body_stays_within_the_cap_with_its_envelope() {
        // Two lines whose escaped sizes add up to exactly the cap: the rest of the request must push the second out.
        static MuseAssistantItem Half(long seq) => Item(seq, T0 + seq) with { Line = new string('a', MuseAssistantPlanner.MaxBatchBytes / 2 - 3) };

        var sends = Plan(Fresh(), Walk(T0 + Minute, Half(1), Half(2))).OfType<MuseAssistantSendLines>().ToList();

        await Assert.That(sends.Count).IsEqualTo(2);
        foreach (var send in sends)
            await Assert.That(Encoding.UTF8.GetByteCount(MuseAssistantHttpPoster.TranscriptBody(send))).IsLessThanOrEqualTo(MuseAssistantPlanner.MaxBatchBytes);
    }

    [Test]
    public async Task A_line_that_fits_the_cap_only_without_the_envelope_is_a_problem() {
        var full = Item(1, T0) with { Line = new string('a', MuseAssistantPlanner.MaxBatchBytes - 3) };

        await Assert.That(MuseAssistantPlanner.OversizeProblem(Walk(T0 + Minute, full), Fresh())).IsNotNull();
    }

    [Test]
    public async Task A_line_whose_escaped_size_exceeds_the_cap_is_a_problem_only_while_it_would_be_sent() {
        var huge = Item(5, T0 + 5, turn: "t9") with { Line = new string('é', 700_000) };
        var walk = Walk(T0 + Minute, Item(4, T0), huge);

        var problem = MuseAssistantPlanner.OversizeProblem(walk, Fresh());

        await Assert.That(problem).IsEqualTo("item seq 5 in turn t9 is too large to send (4200003 bytes; a request carries at most 4 MiB)");
        await Assert.That(MuseAssistantPlanner.OversizeProblem(walk, Fresh() with { Watermark = 5 })).IsNull();
        await Assert.That(MuseAssistantPlanner.OversizeProblem(walk, Fresh(MuseAssistantGrantBoundaries.AfterSeq, 5))).IsNull();
        await Assert.That(MuseAssistantPlanner.OversizeProblem(Walk(T0, huge with { Role = "developer" }), Fresh())).IsNull();
        await Assert.That(MuseAssistantPlanner.OversizeProblem(Walk(T0, Item(1, T0)), Fresh())).IsNull();
    }

    [Test]
    public async Task A_trailing_developer_item_is_covered_by_the_last_batch_and_needs_no_extra_step() {
        var actions = Plan(Fresh(), Walk(T0 + Minute, Item(1, T0), Item(2, T0 + 1, "developer")));

        await Assert.That(Kinds(actions)).IsEquivalentTo([nameof(MuseAssistantStartSession), nameof(MuseAssistantSendLines)], CollectionOrdering.Matching);
        await Assert.That(actions[^1].After.Watermark).IsEqualTo(2);
        await Assert.That(actions[^1].After.LastSelectedAtMs).IsEqualTo(T0);
    }

    [Test]
    public async Task Coverage_needs_has_more_false_or_an_item_at_or_below_the_watermark_or_while_unwatermarked_the_grant_floor() {
        var partial = Walk(T0, Item(50, T0), Item(60, T0 + 1)) with { OldestHasMore = true };

        await Assert.That(MuseAssistantPlanner.CoverageProblem(partial with { OldestHasMore = false }, Fresh())).IsNull();
        await Assert.That(MuseAssistantPlanner.CoverageProblem(partial, Fresh() with { Watermark = 60 })).IsNull();
        await Assert.That(MuseAssistantPlanner.CoverageProblem(partial, Fresh() with { Watermark = 40 })).IsNotNull();
        await Assert.That(MuseAssistantPlanner.CoverageProblem(partial, Fresh(MuseAssistantGrantBoundaries.AfterSeq, 55))).IsNull();
        await Assert.That(MuseAssistantPlanner.CoverageProblem(partial, Fresh(MuseAssistantGrantBoundaries.AfterSeq, 45))).IsNotNull();
        await Assert.That(MuseAssistantPlanner.CoverageProblem(partial, Fresh())).IsNotNull();
        await Assert.That(MuseAssistantPlanner.CoverageProblem(Walk(T0) with { OldestHasMore = true }, Fresh() with { Watermark = 1 })).IsNotNull();
    }

    [Test]
    public async Task Closure_ends_whatever_is_open_replaying_a_pending_start_first() {
        var pendingStart = Fresh() with { Open = new MuseAssistantOpenSession(Id(1), 1, T0, true, Home, MuseAssistantSessionPhases.PendingStart, null) };
        var started      = Fresh() with { Watermark = 2, LastSelectedAtMs = T0 + 1, Open = new MuseAssistantOpenSession(Id(1), 1, T0, true, Home, MuseAssistantSessionPhases.Started, null) };

        var fromPending = MuseAssistantPlanner.Closure(pendingStart);
        var fromStarted = MuseAssistantPlanner.Closure(started);

        await Assert.That(Kinds(fromPending)).IsEquivalentTo([nameof(MuseAssistantStartSession), nameof(MuseAssistantEndSession)], CollectionOrdering.Matching);
        await Assert.That(((MuseAssistantEndSession)fromPending[1]).EndedAtMs).IsEqualTo(T0);
        await Assert.That(Kinds(fromStarted)).IsEquivalentTo([nameof(MuseAssistantEndSession)]);
        await Assert.That(((MuseAssistantEndSession)fromStarted[0]).EndedAtMs).IsEqualTo(T0 + 1);
        await Assert.That(fromStarted[0].Intent!.Open!.Phase).IsEqualTo(MuseAssistantSessionPhases.PendingEnd);
        await Assert.That(fromStarted[0].After.Open).IsNull();
        await Assert.That(MuseAssistantPlanner.Closure(Fresh())).IsEmpty();
    }

    [Test]
    public async Task Replay_reposts_only_the_recorded_intent() {
        var pendingStart = Fresh() with { Open = new MuseAssistantOpenSession(Id(1), 1, T0, true, Home, MuseAssistantSessionPhases.PendingStart, null) };
        var started      = pendingStart with { Open = pendingStart.Open! with { Phase = MuseAssistantSessionPhases.Started } };

        await Assert.That(Kinds(MuseAssistantPlanner.Replay(pendingStart))).IsEquivalentTo([nameof(MuseAssistantStartSession)]);
        await Assert.That(MuseAssistantPlanner.Replay(pendingStart)[0].After.Open!.Phase).IsEqualTo(MuseAssistantSessionPhases.Started);
        await Assert.That(MuseAssistantPlanner.Replay(started)).IsEmpty();
        await Assert.That(MuseAssistantPlanner.Replay(Fresh())).IsEmpty();
    }
}
