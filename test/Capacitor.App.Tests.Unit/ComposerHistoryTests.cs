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
