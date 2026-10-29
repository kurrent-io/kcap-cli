using System.Globalization;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

public class ComposerHistoryTests {
    static readonly string[] None = [];

    static ComposerHistory Seen(params string[] submitted) {
        var history = new ComposerHistory();
        foreach (var text in submitted) history.Observe(text);
        return history;
    }

    static async Task AssertRecallOrder(ComposerHistory history, string[] unechoed, params string[] newestFirst) {
        var current = "";
        foreach (var expected in newestFirst) {
            current = history.Older(current, unechoed)!;
            await Assert.That(current).IsEqualTo(expected);
        }
        await Assert.That(history.Older(current, unechoed)).IsNull();
    }

    [Test]
    public async Task Older_walks_back_from_the_newest_and_stops_at_the_oldest() {
        await AssertRecallOrder(Seen("first", "second"), None, "second", "first");
    }

    [Test]
    public async Task Unechoed_sends_are_newer_than_everything_the_transcript_recorded() {
        await AssertRecallOrder(Seen("old"), ["sent"], "sent", "old");
    }

    [Test]
    public async Task Older_leaves_a_typed_draft_alone() {
        await Assert.That(Seen("first").Older("typing", None)).IsNull();
    }

    [Test]
    public async Task Older_with_nothing_to_recall_recalls_nothing() {
        await Assert.That(new ComposerHistory().Older("", None)).IsNull();
    }

    [Test]
    public async Task Newer_walks_forward_and_then_restores_the_draft() {
        var history = Seen("first", "second");
        await Assert.That(history.Older("  ", None)).IsEqualTo("second");
        await Assert.That(history.Older("second", None)).IsEqualTo("first");
        await Assert.That(history.Newer("first", None)).IsEqualTo("second");
        await Assert.That(history.Newer("second", None)).IsEqualTo("  ");
        await Assert.That(history.Newer("  ", None)).IsNull();
    }

    [Test]
    public async Task Newer_outside_navigation_does_nothing() {
        var history = Seen("first");
        await Assert.That(history.Newer("", None)).IsNull();
        await Assert.That(history.Newer("typing", None)).IsNull();
    }

    /// An edited recall is the user's own draft: neither key steps away from it.
    [Test]
    public async Task An_edit_to_a_recalled_prompt_ends_navigation() {
        var history = Seen("first", "second");
        await Assert.That(history.Older("", None)).IsEqualTo("second");
        await Assert.That(history.Older("second edited", None)).IsNull();
        await Assert.That(history.Newer("second edited", None)).IsNull();
    }

    [Test]
    public async Task Observe_collapses_consecutive_duplicates_and_skips_blanks() {
        await AssertRecallOrder(Seen("a", "a", "  ", "b", "a"), None, "a", "b", "a");
    }

    /// The echo moves a send from the unechoed tail into the transcript's entries; a recall
    /// standing on it keeps its place.
    [Test]
    public async Task A_send_echoed_mid_recall_keeps_the_recall_in_place() {
        var history = Seen("old");
        await Assert.That(history.Older("", ["sent"])).IsEqualTo("sent");
        history.Observe("sent");
        await Assert.That(history.Older("sent", None)).IsEqualTo("old");
        await Assert.That(history.Newer("old", None)).IsEqualTo("sent");
        await Assert.That(history.Newer("sent", None)).IsEqualTo("");
    }

    [Test]
    public async Task An_arrival_during_a_recall_does_not_interrupt_it() {
        var history = Seen("first", "second");
        await Assert.That(history.Older("", None)).IsEqualTo("second");
        history.Observe("third");
        await Assert.That(history.Older("second", None)).IsEqualTo("first");
        await Assert.That(history.Newer("first", None)).IsEqualTo("second");
        await Assert.That(history.Newer("second", None)).IsEqualTo("third");
    }

    /// The shown entry can be pushed out the front; ↓ then resumes at the oldest entry left.
    [Test]
    public async Task A_recall_evicted_past_capacity_resumes_at_the_oldest_left() {
        var history = Seen(Enumerable.Range(0, ComposerHistory.Capacity).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray());
        var current = "";
        for (var i = 0; i < ComposerHistory.Capacity; i++) current = history.Older(current, None)!;
        await Assert.That(current).IsEqualTo("0");
        history.Observe("new");
        history.Observe("newer");
        await Assert.That(history.Older(current, None)).IsNull();
        await Assert.That(history.Newer(current, None)).IsEqualTo("2");
    }

    [Test]
    public async Task Observe_drops_the_oldest_past_capacity() {
        var history = Seen(Enumerable.Range(0, ComposerHistory.Capacity + 1).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray());
        var current = "";
        for (var i = 0; i < ComposerHistory.Capacity; i++) current = history.Older(current, None)!;
        await Assert.That(current).IsEqualTo("1");
        await Assert.That(history.Older(current, None)).IsNull();
    }

    /// A replay under a recall finds the recalled turn again once it is replayed.
    [Test]
    public async Task A_replay_during_a_recall_picks_the_recall_back_up() {
        var history = Seen("first", "second");
        await Assert.That(history.Older("", None)).IsEqualTo("second");
        history.ClearForReplay();
        foreach (var text in new[] { "first", "second", "third" }) history.Observe(text);
        await Assert.That(history.Older("second", None)).IsEqualTo("first");
        await Assert.That(history.Newer("first", None)).IsEqualTo("second");
        await Assert.That(history.Newer("second", None)).IsEqualTo("third");
    }

    [Test]
    public async Task A_recall_whose_turn_is_gone_still_steps_back_to_the_draft() {
        var history = Seen("first", "second");
        await Assert.That(history.Older("", None)).IsEqualTo("second");
        history.ClearForReplay();
        await Assert.That(history.Newer("second", None)).IsEqualTo("");
        await Assert.That(history.Older("", None)).IsNull();
    }

    [Test]
    public async Task Clear_ends_a_recall_on_screen() {
        var history = Seen("first", "second");
        await Assert.That(history.Older("", None)).IsEqualTo("second");
        history.Clear();
        history.Observe("second");
        await Assert.That(history.Newer("second", None)).IsNull();
        await Assert.That(history.Older("second", None)).IsNull();
    }
}
