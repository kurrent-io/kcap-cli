using System.Globalization;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

public class ComposerHistoryTests {
    static ComposerHistory With(params string[] sent) {
        var history = new ComposerHistory();
        foreach (var text in sent) history.Record(text);
        return history;
    }

    [Test]
    public async Task Older_on_an_empty_composer_recalls_the_last_sent_prompt() {
        var history = With("first", "second");
        await Assert.That(history.Older("")).IsEqualTo("second");
    }

    [Test]
    public async Task Older_walks_back_and_stops_at_the_oldest() {
        var history = With("first", "second");
        await Assert.That(history.Older("")).IsEqualTo("second");
        await Assert.That(history.Older("second")).IsEqualTo("first");
        await Assert.That(history.Older("first")).IsNull();
    }

    [Test]
    public async Task Older_leaves_a_typed_draft_alone() {
        var history = With("first");
        await Assert.That(history.Older("typing")).IsNull();
    }

    [Test]
    public async Task Older_with_nothing_sent_recalls_nothing() {
        await Assert.That(new ComposerHistory().Older("")).IsNull();
    }

    [Test]
    public async Task Newer_walks_forward_and_then_restores_the_draft() {
        var history = With("first", "second");
        await Assert.That(history.Older("  ")).IsEqualTo("second");
        await Assert.That(history.Older("second")).IsEqualTo("first");
        await Assert.That(history.Newer("first")).IsEqualTo("second");
        await Assert.That(history.Newer("second")).IsEqualTo("  ");
        await Assert.That(history.Newer("  ")).IsNull();
    }

    [Test]
    public async Task Newer_outside_navigation_does_nothing() {
        var history = With("first");
        await Assert.That(history.Newer("")).IsNull();
        await Assert.That(history.Newer("typing")).IsNull();
    }

    /// An edited recall is the user's own draft: neither key steps away from it.
    [Test]
    public async Task An_edit_to_a_recalled_prompt_ends_navigation() {
        var history = With("first", "second");
        await Assert.That(history.Older("")).IsEqualTo("second");
        await Assert.That(history.Older("second edited")).IsNull();
        await Assert.That(history.Newer("second edited")).IsNull();
    }

    [Test]
    public async Task Record_collapses_consecutive_duplicates() {
        var history = With("a", "a", "b", "a");
        await Assert.That(history.Older("")).IsEqualTo("a");
        await Assert.That(history.Older("a")).IsEqualTo("b");
        await Assert.That(history.Older("b")).IsEqualTo("a");
        await Assert.That(history.Older("a")).IsNull();
    }

    [Test]
    public async Task Record_ends_navigation() {
        var history = With("first");
        await Assert.That(history.Older("")).IsEqualTo("first");
        history.Record("third");
        await Assert.That(history.Newer("first")).IsNull();
        await Assert.That(history.Older("")).IsEqualTo("third");
    }

    static ComposerHistory Seen(params string[] shown) {
        var history = new ComposerHistory();
        foreach (var text in shown) history.Observe(text);
        return history;
    }

    static async Task AssertRecallOrder(ComposerHistory history, params string[] newestFirst) {
        var current = "";
        foreach (var expected in newestFirst) {
            current = history.Older(current)!;
            await Assert.That(current).IsEqualTo(expected);
        }
        await Assert.That(history.Older(current)).IsNull();
    }

    [Test]
    public async Task Observe_settles_the_echo_of_a_sent_prompt_and_skips_blanks() {
        var history = With("first");
        history.Observe("first");
        history.Observe("  ");
        await AssertRecallOrder(history, "first");
    }

    /// The first transcript read can land after a send made while it was in flight.
    [Test]
    public async Task A_turn_the_transcript_shows_sorts_ahead_of_an_unechoed_send() {
        var history = With("new");
        history.Observe("old");
        history.Observe("new");
        await AssertRecallOrder(history, "new", "old");
    }

    [Test]
    public async Task Another_turn_between_a_send_and_its_echo_leaves_one_entry_each() {
        var history = With("mine");
        history.Observe("theirs");
        history.Observe("mine");
        await AssertRecallOrder(history, "mine", "theirs");
    }

    /// A slash command never echoes as a user turn; a later send's echo settles it in place.
    [Test]
    public async Task A_send_that_never_echoes_keeps_its_place() {
        var history = With("/compact", "hi");
        history.Observe("hi");
        history.Observe("later");
        await AssertRecallOrder(history, "later", "hi", "/compact");
    }

    [Test]
    public async Task Observe_leaves_a_recall_in_progress_alone() {
        var history = Seen("first", "second");
        await Assert.That(history.Older("")).IsEqualTo("second");
        history.Observe("third");
        await Assert.That(history.Older("second")).IsEqualTo("first");
        await Assert.That(history.Newer("first")).IsEqualTo("second");
        await Assert.That(history.Newer("second")).IsEqualTo("third");
    }

    /// The recalled entry can be pushed out the front, and more after it; ↓ then resumes at the
    /// oldest entry left.
    [Test]
    public async Task Observe_past_capacity_keeps_the_recall_cursor_in_step() {
        var history = Seen(Enumerable.Range(0, ComposerHistory.Capacity).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray());
        var current = "";
        for (var i = 0; i < ComposerHistory.Capacity; i++) current = history.Older(current)!;
        await Assert.That(current).IsEqualTo("0");
        history.Observe("new");
        history.Observe("newer");
        await Assert.That(history.Older(current)).IsNull();
        await Assert.That(history.Newer(current)).IsEqualTo("2");
    }

    [Test]
    public async Task Clear_drops_the_shown_turns_and_keeps_unechoed_sends() {
        var history = Seen("old");
        history.Record("sent");
        history.Clear();
        await AssertRecallOrder(history, "sent");
        history.Observe("old");
        await AssertRecallOrder(history, "sent", "old");
    }

    /// A replay under a recall leaves it navigable: ↑ reaches the replayed turns and ↓ the draft.
    [Test]
    public async Task Clear_during_a_recall_keeps_it_navigable() {
        var history = Seen("first", "second");
        await Assert.That(history.Older("")).IsEqualTo("second");
        history.Clear();
        history.Observe("first");
        history.Observe("replayed");
        await Assert.That(history.Older("second")).IsEqualTo("replayed");
        await Assert.That(history.Newer("replayed")).IsEqualTo("");
        await Assert.That(history.Older("")).IsEqualTo("replayed");
        history.Clear();
        await Assert.That(history.Newer("replayed")).IsEqualTo("");
    }

    [Test]
    public async Task Record_drops_the_oldest_past_capacity() {
        var history = With(Enumerable.Range(0, ComposerHistory.Capacity + 1).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToArray());
        var current = "";
        string? recalled = null;
        for (var i = 0; i < ComposerHistory.Capacity; i++) {
            recalled = history.Older(current);
            await Assert.That(recalled).IsNotNull();
            current = recalled!;
        }
        await Assert.That(recalled).IsEqualTo("1");
        await Assert.That(history.Older(current)).IsNull();
    }
}
