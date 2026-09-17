using Capacitor.App.GitHubHtml;

namespace Capacitor.App.Tests.Unit.GitHubHtml;

public class HtmlTokenizerTests {
    static string Shape(HtmlTokenization result) => string.Join(" ", result.Tokens.Select(t => t.Kind switch {
        HtmlTokenKind.Text    => $"text({t.Text})",
        HtmlTokenKind.Comment => "comment",
        HtmlTokenKind.OpenTag => $"<{t.Name}{string.Concat(t.Attributes.OrderBy(a => a.Key).Select(a => $" {a.Key}={a.Value}"))}>",
        _                     => $"</{t.Name}>",
    }));

    [Test]
    public async Task Tags_text_and_comments_come_out_in_order() {
        var result = HtmlTokenizer.Tokenize("a<B>x</b><!-- c -->y");
        await Assert.That(Shape(result)).IsEqualTo("text(a) <b> text(x) </b> comment text(y)");
        await Assert.That(result.Malformed).IsFalse();
    }

    /// Pins the three attribute quotings, a bare boolean attribute, entity decoding in a value,
    /// and a trailing slash leaving the tag an open tag.
    [Test]
    public async Task Attributes_read_in_every_quoting() {
        var result = HtmlTokenizer.Tokenize("<img src=\"https://h/a?x=1&amp;y=2\" alt='two words' width=20 />" + "<details open>");
        await Assert.That(Shape(result)).IsEqualTo("<img alt=two words src=https://h/a?x=1&y=2 width=20> <details open=>");
        await Assert.That(result.Tokens[0].Attribute("ALT")).IsEqualTo("two words");
        await Assert.That(result.Tokens[0].Attribute("missing")).IsNull();
    }

    [Test]
    public async Task A_tag_may_span_lines() {
        var result = HtmlTokenizer.Tokenize("<span\n title=\"x\">");
        await Assert.That(Shape(result)).IsEqualTo("<span title=x>");
    }

    [Test]
    public async Task A_lone_angle_bracket_is_text_and_not_malformed() {
        var result = HtmlTokenizer.Tokenize("a < b and 1<2");
        await Assert.That(Shape(result)).IsEqualTo("text(a < b and 1<2)");
        await Assert.That(result.Malformed).IsFalse();
    }

    /// Pins the no-throw contract: input that starts a tag or a comment and never finishes comes
    /// back as text and marks the result.
    [Test]
    [Arguments("<a href=\"")]
    [Arguments("<a")]
    [Arguments("</")]
    [Arguments("</b")]
    [Arguments("<!-- never closed")]
    [Arguments("x <b y")]
    public async Task Unfinished_input_is_text_and_malformed(string html) {
        var result = HtmlTokenizer.Tokenize(html);
        await Assert.That(result.Tokens.All(t => t.Kind == HtmlTokenKind.Text)).IsTrue();
        await Assert.That(string.Concat(result.Tokens.Select(t => t.Text))).IsEqualTo(html);
        await Assert.That(result.Malformed).IsTrue();
    }

    [Test]
    public async Task A_bare_angle_bracket_at_the_end_is_plain_text() {
        var result = HtmlTokenizer.Tokenize("x <");
        await Assert.That(Shape(result)).IsEqualTo("text(x <)");
        await Assert.That(result.Malformed).IsFalse();
    }

    [Test]
    public async Task Whitespace_only_text_is_recognised() {
        var result = HtmlTokenizer.Tokenize("<b> \n </b>");
        await Assert.That(result.Tokens[1].IsWhitespace).IsTrue();
        await Assert.That(result.Tokens[0].IsWhitespace).IsFalse();
    }

    /// Pins linear work: every one of these tag starts scans a quoted value that closes only at the
    /// very end and then fails on the `!`, which costs the square of the input unless the quote
    /// search is remembered — minutes at this size, against milliseconds.
    [Test]
    [Timeout(10_000)]
    public async Task Repeated_unclosed_quotes_stay_linear(CancellationToken _) {
        var html = string.Concat(Enumerable.Repeat("<a x=\"", 200_000)) + "\"!>";
        var result = HtmlTokenizer.Tokenize(html);
        await Assert.That(result.Malformed).IsTrue();
        await Assert.That(result.Tokens.All(t => t.Kind == HtmlTokenKind.Text)).IsTrue();
    }
}
