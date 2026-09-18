using Capacitor.App.GitHubHtml;

namespace Capacitor.App.Tests.Unit.GitHubHtml;

public class HtmlTagsTests {
    [Test]
    [Arguments("img", HtmlTagClass.Void)]
    [Arguments("br", HtmlTagClass.Void)]
    [Arguments("details", HtmlTagClass.Structural)]
    [Arguments("summary", HtmlTagClass.Paired)]
    [Arguments("pre", HtmlTagClass.Paired)]
    [Arguments("a", HtmlTagClass.Paired)]
    [Arguments("kbd", HtmlTagClass.Paired)]
    [Arguments("strike", HtmlTagClass.Paired)]
    [Arguments("div", HtmlTagClass.Unknown)]
    [Arguments("table", HtmlTagClass.Unknown)]
    [Arguments("script", HtmlTagClass.Unknown)]
    public async Task Tags_fall_into_their_class(string name, HtmlTagClass expected) =>
        await Assert.That(HtmlTags.Classify(name)).IsEqualTo(expected);

    [Test]
    [Arguments("b", '*', 2)]
    [Arguments("strong", '*', 2)]
    [Arguments("i", '*', 1)]
    [Arguments("em", '*', 1)]
    [Arguments("del", '~', 2)]
    [Arguments("s", '~', 2)]
    [Arguments("strike", '~', 2)]
    [Arguments("ins", '+', 2)]
    [Arguments("sub", '~', 1)]
    [Arguments("sup", '^', 1)]
    public async Task Formatting_tags_map_to_emphasis_delimiters(string name, char delimiter, int count) {
        await Assert.That(HtmlTags.TryEmphasis(name, out var actualDelimiter, out var actualCount)).IsTrue();
        await Assert.That(actualDelimiter).IsEqualTo(delimiter);
        await Assert.That(actualCount).IsEqualTo(count);
    }

    /// Pins which paired tags a paragraph may hold: `summary` and `pre` pair but are block-only.
    [Test]
    public async Task Block_only_tags_are_not_inline() {
        await Assert.That(HtmlTags.IsInline("summary")).IsFalse();
        await Assert.That(HtmlTags.IsInline("pre")).IsFalse();
        await Assert.That(HtmlTags.IsInline("a")).IsTrue();
        await Assert.That(HtmlTags.IsInline("code")).IsTrue();
        await Assert.That(HtmlTags.IsCode("samp")).IsTrue();
        await Assert.That(HtmlTags.IsCode("b")).IsFalse();
    }

    [Test]
    public async Task Whitespace_runs_collapse_to_one_space() {
        await Assert.That(InlineText.Collapse(" a \r\n\t b  ")).IsEqualTo(" a b ");
        await Assert.That(InlineText.Collapse(null)).IsEqualTo("");
    }
}
