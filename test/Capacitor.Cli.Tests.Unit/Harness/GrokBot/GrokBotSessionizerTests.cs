using Capacitor.Cli.Harness.GrokBot;

namespace Capacitor.Cli.Tests.Unit.Harness.GrokBot;

/// <summary>Pins how a Bot's endless thread is cut into sessions and when an entry may be sent.</summary>
public class GrokBotSessionizerTests {
    const string Agent = "4d30ec7f-1027-4a23-86d7-d70f37e5bfec";
    static readonly TimeSpan Gap = TimeSpan.FromHours(2);
    const long Hour = 3_600_000;
    const long T0   = 1_790_000_000_000;

    static GrokBotEntry Entry(int seq, long ts, bool streaming = false, bool awaiting = false) =>
        new($"e{seq}", seq, ts, streaming, awaiting, $$"""{"id":"e{{seq}}","seq":{{seq}}}""");

    static IReadOnlyList<GrokBotAction> Plan(GrokBotBotState state, long now, bool running = false, params GrokBotEntry[] entries) =>
        GrokBotSessionizer.Plan(Agent, state, entries, now, running, Gap);

    [Test]
    public async Task First_entries_open_a_session_and_send_them_in_seq_order() {
        var actions = Plan(GrokBotBotState.Empty, T0 + 10, entries: [Entry(5, T0 + 5), Entry(3, T0)]);

        await Assert.That(actions.Count).IsEqualTo(2);
        var start = (GrokBotStartSession)actions[0];
        var send  = (GrokBotSendLines)actions[1];

        await Assert.That(start.SessionId).IsEqualTo(GrokBotSessionizer.SessionId(Agent, "e3"));
        await Assert.That(start.StartedAtMs).IsEqualTo(T0);
        await Assert.That(send.Seqs).IsEquivalentTo([3, 5]);
        await Assert.That(send.After.LastSeq).IsEqualTo(5);
        await Assert.That(send.After.Open!.LastEntryMs).IsEqualTo(T0 + 5);
    }

    [Test]
    public async Task Start_state_does_not_advance_the_seq_so_a_failed_send_is_retried() {
        var start = (GrokBotStartSession)Plan(GrokBotBotState.Empty, T0, entries: [Entry(1, T0)])[0];

        await Assert.That(start.After.LastSeq).IsEqualTo(-1);
        await Assert.That(start.After.Open).IsNotNull();
    }

    [Test]
    public async Task An_entry_after_the_gap_ends_the_open_session_and_opens_another() {
        var first = Plan(GrokBotBotState.Empty, T0, entries: [Entry(1, T0)])[^1].After;

        var actions = Plan(first, T0 + 3 * Hour, entries: [Entry(2, T0 + 3 * Hour)]);

        await Assert.That(actions.Select(a => a.GetType().Name))
            .IsEquivalentTo([nameof(GrokBotEndSession), nameof(GrokBotStartSession), nameof(GrokBotSendLines)]);
        var end = (GrokBotEndSession)actions[0];
        await Assert.That(end.SessionId).IsEqualTo(first.Open!.SessionId);
        await Assert.That(end.EndedAtMs).IsEqualTo(T0);
        await Assert.That(((GrokBotStartSession)actions[1]).SessionId).IsEqualTo(GrokBotSessionizer.SessionId(Agent, "e2"));
    }

    [Test]
    public async Task Entries_inside_the_gap_stay_in_one_session() {
        var actions = Plan(GrokBotBotState.Empty, T0 + Hour, entries: [Entry(1, T0), Entry(2, T0 + Hour)]);

        await Assert.That(actions.OfType<GrokBotStartSession>().Count()).IsEqualTo(1);
        await Assert.That(actions.OfType<GrokBotEndSession>()).IsEmpty();
    }

    [Test]
    public async Task An_idle_thread_ends_its_session_at_the_last_entry_once_the_gap_passes() {
        var open = Plan(GrokBotBotState.Empty, T0, entries: [Entry(1, T0)])[^1].After;

        await Assert.That(Plan(open, T0 + Hour)).IsEmpty();

        var end = (GrokBotEndSession)Plan(open, T0 + 2 * Hour)[0];
        await Assert.That(end.EndedAtMs).IsEqualTo(T0);
        await Assert.That(end.After.Open).IsNull();
        await Assert.That(end.After.LastSeq).IsEqualTo(1);
    }

    [Test]
    public async Task A_running_turn_keeps_an_idle_session_open() {
        var open = Plan(GrokBotBotState.Empty, T0, entries: [Entry(1, T0)])[^1].After;

        await Assert.That(Plan(open, T0 + 3 * Hour, running: true)).IsEmpty();
    }

    [Test]
    public async Task A_streaming_entry_holds_back_itself_and_everything_after_it() {
        var actions = Plan(GrokBotBotState.Empty, T0 + 2, entries: [Entry(1, T0), Entry(2, T0 + 1, streaming: true), Entry(3, T0 + 2)]);

        await Assert.That(((GrokBotSendLines)actions[^1]).Seqs).IsEquivalentTo([1]);
    }

    [Test]
    public async Task An_unanswered_widget_waits_unless_a_later_entry_follows_it() {
        var waiting = Plan(GrokBotBotState.Empty, T0 + 1, entries: [Entry(1, T0), Entry(2, T0 + 1, awaiting: true)]);
        await Assert.That(((GrokBotSendLines)waiting[^1]).Seqs).IsEquivalentTo([1]);

        var followed = Plan(GrokBotBotState.Empty, T0 + 2, entries: [Entry(1, T0), Entry(2, T0 + 1, awaiting: true), Entry(3, T0 + 2)]);
        await Assert.That(((GrokBotSendLines)followed[^1]).Seqs).IsEquivalentTo([1, 2, 3]);
    }

    [Test]
    public async Task An_unsettled_entry_older_than_the_gap_is_sent_as_it_stands() {
        var actions = Plan(GrokBotBotState.Empty, T0 + 3 * Hour, entries: [Entry(1, T0, awaiting: true)]);

        await Assert.That(actions.OfType<GrokBotSendLines>().Single().Seqs).IsEquivalentTo([1]);
        await Assert.That(actions[^1]).IsTypeOf<GrokBotEndSession>();
    }

    [Test]
    public async Task Entries_already_sent_are_never_planned_again() {
        var sent = Plan(GrokBotBotState.Empty, T0 + 1, entries: [Entry(1, T0), Entry(2, T0 + 1)])[^1].After;

        await Assert.That(Plan(sent, T0 + 1, entries: [Entry(1, T0), Entry(2, T0 + 1)])).IsEmpty();
    }

    [Test]
    public async Task Long_runs_are_split_into_batches_each_carrying_its_own_progress() {
        var entries = Enumerable.Range(1, 250).Select(i => Entry(i, T0 + i)).ToArray();

        var batches = GrokBotSessionizer.Plan(Agent, GrokBotBotState.Empty, entries, T0 + 250, false, Gap)
            .OfType<GrokBotSendLines>().ToList();

        await Assert.That(batches.Select(b => b.Lines.Length)).IsEquivalentTo([100, 100, 50]);
        await Assert.That(batches.Select(b => b.After.LastSeq)).IsEquivalentTo([100, 200, 250]);
    }

    [Test]
    public async Task Session_ids_are_32_lowercase_hex_and_stable() {
        var id = GrokBotSessionizer.SessionId(Agent, "t2u");

        await Assert.That(id).IsEqualTo(GrokBotSessionizer.SessionId(Agent, "t2u"));
        await Assert.That(id.Length).IsEqualTo(32);
        await Assert.That(id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')).IsTrue();
        await Assert.That(GrokBotSessionizer.SessionId("other", "t2u")).IsNotEqualTo(id);
    }
}
