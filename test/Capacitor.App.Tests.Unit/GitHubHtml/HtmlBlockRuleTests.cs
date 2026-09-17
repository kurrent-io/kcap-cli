using Markdig.Syntax;

namespace Capacitor.App.Tests.Unit.GitHubHtml;

public class HtmlBlockRuleTests {
    static async Task Untouched(string markdown) {
        await Assert.That(Trees.Shape(markdown)).StartsWith("doc(html(");
        await Assert.That(Trees.Dump(markdown)).IsEqualTo(Trees.Shape(markdown));
    }

    [Test]
    public async Task An_image_alone_on_a_line_is_a_block_and_converts() {
        const string markdown = "<img src=\"https://img.shields.io/badge/Medium-634FD1\" height=\"20px\" alt=\"Remediation recommended\">";
        await Assert.That(Trees.Shape(markdown)).StartsWith("doc(html(");
        await Assert.That(Trees.Dump(markdown)).IsEqualTo("doc(p(img[https://img.shields.io/badge/Medium-634FD1]('Remediation recommended')))");
    }

    /// Whitespace collapses as HTML does and is trimmed at the paragraph's edges, inside the
    /// formatting that opens and closes it too.
    [Test]
    public async Task Inline_content_becomes_a_paragraph_with_collapsed_whitespace() {
        const string markdown = "<b>\nbold   text\n</b>";
        await Assert.That(Trees.Shape(markdown)).IsEqualTo("doc(html('<b>\\nbold   text\\n</b>'))");
        await Assert.That(Trees.Dump(markdown)).IsEqualTo("doc(p(em*2('bold text')))");
    }

    [Test]
    public async Task One_unknown_tag_rejects_the_whole_block() {
        await Untouched("<b>\nbold <span>x</span>\n</b>");
        await Assert.That(Trees.Dump("<b>\nbold x\n</b>")).IsEqualTo("doc(p(em*2('bold x')))");
    }

    [Test]
    [Arguments("<b>\n<i>x</b></i>")]
    [Arguments("<b>\nx")]
    [Arguments("</b>\nx")]
    [Arguments("<summary>S</summary>")]
    [Arguments("<b>\n<pre>x</pre>\n</b>")]
    [Arguments("<b>\nx</br>\n</b>")]
    [Arguments("<b>\nx</img>\n</b>")]
    [Arguments("<b>\nx <a href=\"\n</b>")]
    public async Task A_block_that_does_not_nest_properly_stays_source(string markdown) => await Untouched(markdown);

    [Test]
    public async Task A_trailing_slash_on_a_paired_tag_is_ignored() {
        await Assert.That(Trees.Dump("<b/>\nx</b>")).IsEqualTo("doc(p(em*2('x')))");
        await Untouched("<b/>\nx");
    }

    [Test]
    public async Task Entities_decode_in_text_and_links_are_mapped_and_hoisted() {
        await Assert.That(Trees.Dump("<b>\nA &amp; B\n</b>")).IsEqualTo("doc(p(em*2('A & B')))");
        await Assert.That(Trees.Dump("<b>\n<a href=\"javascript:alert(1)\">x</a>\n</b>")).IsEqualTo("doc(p(link[javascript:alert(1)](em*2('x'))))");
    }

    [Test]
    public async Task Void_tags_convert_inside_a_block() =>
        await Assert.That(Trees.Dump("<b>\na<br>b<br/>c\n</b>")).IsEqualTo("doc(p(em*2('a',br,'b',br,'c')))");

    /// Pins `pre`: whitespace and inner formatting kept, entities decoded, the line end after the
    /// open tag and the one before the close tag dropped, every other line end a hard break.
    [Test]
    public async Task Pre_keeps_whitespace_formatting_and_entities() {
        const string markdown = "<pre>\nThe <b><i>repo&#x27;s</i></b> begins\n  indented\n</pre>";
        await Assert.That(Trees.Shape(markdown)).StartsWith("doc(html(");
        await Assert.That(Trees.Dump(markdown)).IsEqualTo("doc(pre('The ',em*2(em*1('repo's')),' begins',br,'  indented'))");
    }

    [Test]
    public async Task Code_inside_pre_splits_at_line_ends() {
        await Assert.That(Trees.Shape("<pre><code>a\n\nb</code></pre>")).StartsWith("doc(html(");
        await Assert.That(Trees.Dump("<pre><code>a\n\nb</code></pre>")).IsEqualTo("doc(pre(code('a'),br,br,code('b')))");
    }

    [Test]
    public async Task Decoded_line_ends_split_too() {
        await Assert.That(Trees.Dump("<pre>a&#10;b</pre>")).IsEqualTo("doc(pre('a',br,'b'))");
        await Assert.That(Trees.Dump("<pre>a&#13;&#10;b</pre>")).IsEqualTo("doc(pre('a',br,'b'))");
        await Assert.That(Trees.Dump("<b>\na&#10;b\n</b>")).IsEqualTo("doc(p(em*2('a b')))");
    }

    [Test]
    public async Task Void_tags_are_accepted_inside_pre() =>
        await Assert.That(Trees.Dump("<pre>a<br>b <img src=\"https://h/i.png\"></pre>")).IsEqualTo("doc(pre('a',br,'b ',img[https://h/i.png]('i.png')))");

    [Test]
    public async Task A_link_inside_formatting_inside_pre_is_hoisted() =>
        await Assert.That(Trees.Dump("<pre><b><a href=\"https://u.example\">x</a></b></pre>")).IsEqualTo("doc(pre(link[https://u.example](em*2('x'))))");

    /// Comments go token by token: a comment-type block keeps the whole of its closing line, so
    /// dropping the block would drop the visible text after the comment.
    [Test]
    public async Task Comments_are_removed_where_the_block_converts() {
        await Assert.That(Trees.Shape("<!-- c -->")).StartsWith("doc(html(");
        await Assert.That(Trees.Dump("<!-- c -->")).IsEqualTo("doc()");
        await Assert.That(Trees.Shape("<!-- m -->Visible text")).StartsWith("doc(html(");
        await Assert.That(Trees.Dump("<!-- m -->Visible text")).IsEqualTo("doc(p('Visible text'))");
        await Assert.That(Trees.Dump("before\n\n<!-- c -->\n\nafter")).IsEqualTo("doc(p('before'),p('after'))");
    }

    [Test]
    public async Task Comments_stay_where_the_block_is_rejected() {
        await Untouched("<div><!-- m --></div>");
        await Untouched("<!-- never closed\ntext");
    }

    /// Pins the block's own fallback at the depth limit: the whole block stays source, where a
    /// paragraph converts pair by pair.
    [Test]
    public async Task A_block_nested_past_the_depth_limit_is_rejected_whole() =>
        await Untouched("<b>\n" + string.Concat(Enumerable.Repeat("<b>", 199)) + "x" + string.Concat(Enumerable.Repeat("</b>", 200)));

    [Test]
    public async Task No_synthesized_text_holds_a_line_end() {
        string[] fixtures = [
            "<b>\nbold\ntext\n</b>", "<pre>\na\r\nb\n</pre>", "<pre><code>a\n\nb</code></pre>", "<pre>a&#13;&#10;b</pre>",
            "<img src=\"https://h/a%0Ab.png\">", "<b>\n<code>a\nb</code>\n</b>", "<img alt=\"two\nlines\" src=\"https://h/x.png\">",
        ];
        foreach (var fixture in fixtures) {
            var document = Trees.Parse(fixture);
            foreach (var literal in document.Descendants<Markdig.Syntax.Inlines.LiteralInline>())
                await Assert.That(literal.Content.ToString().AsSpan().IndexOfAny('\r', '\n')).IsEqualTo(-1);
            foreach (var code in document.Descendants<Markdig.Syntax.Inlines.CodeInline>())
                await Assert.That(code.Content.AsSpan().IndexOfAny('\r', '\n')).IsEqualTo(-1);
        }
    }
}
