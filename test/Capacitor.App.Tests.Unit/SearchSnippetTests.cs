using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

public class SearchSnippetTests {
    [Test]
    public async Task Parses_marked_spans_and_drops_the_markers() {
        var runs = SearchSnippet.Parse("a\u0002loc\u0003al");

        await Assert.That(runs).IsEquivalentTo(new[] {
            new SnippetRun("a", false), new SnippetRun("loc", true), new SnippetRun("al", false)
        });
    }

    [Test]
    public async Task Unterminated_marker_runs_to_the_end() {
        var runs = SearchSnippet.Parse("a\u0002bc");

        await Assert.That(runs).IsEquivalentTo(new[] { new SnippetRun("a", false), new SnippetRun("bc", true) });
    }

    [Test]
    public async Task Null_and_plain_text_give_one_unmatched_run() {
        await Assert.That(SearchSnippet.Parse(null)).IsEmpty();
        await Assert.That(SearchSnippet.Parse("plain")).IsEquivalentTo(new[] { new SnippetRun("plain", false) });
    }

    [Test]
    public async Task Adjacent_runs_with_the_same_flag_merge() {
        var runs = SearchSnippet.Parse("\u0002a\u0003\u0002b\u0003");

        await Assert.That(runs).IsEquivalentTo(new[] { new SnippetRun("ab", true) });
    }
}
