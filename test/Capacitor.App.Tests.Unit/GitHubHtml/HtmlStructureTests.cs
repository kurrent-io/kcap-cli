using Capacitor.App.GitHubHtml;
using Markdig.Extensions.Tables;
using Markdig.Syntax;

namespace Capacitor.App.Tests.Unit.GitHubHtml;

/// The structural elements: what each becomes, how they pair across blocks, and which
/// placements are refused.
public class HtmlStructureTests {
    static async Task Untouched(string markdown) {
        await Assert.That(Trees.Shape(markdown)).StartsWith("doc(html(");
        await Assert.That(Trees.Dump(markdown)).IsEqualTo(Trees.Shape(markdown));
    }

    [Test]
    public async Task A_heading_becomes_a_heading_block_with_its_inlines() {
        await Assert.That(Trees.Dump("<h3>PR Summary by Qodo</h3>")).IsEqualTo("doc(h3('PR Summary by Qodo'))");
        const string badge = "<h2><a href=\"https://u.example\"><picture><source srcset=\"https://h/d.svg\"><img alt=\"Retrigger\" src=\"https://h/r.svg\" align=\"right\"></picture></a>Confidence Score: 4/5</h2>";
        await Assert.That(Trees.Dump(badge)).IsEqualTo("doc(h2(link[https://u.example](img[https://h/r.svg]('Retrigger')),'Confidence Score: 4/5'))");
    }

    /// A heading is a leaf: one that spans blocks has no inline content to hold.
    [Test]
    public async Task A_heading_split_across_blocks_stays_source() =>
        await Assert.That(Trees.Dump("<h2>\n\ntext\n\n</h2>")).IsEqualTo("doc(html('<h2>'),p('text'),html('</h2>'))");

    [Test]
    public async Task Paragraph_and_div_elements_are_transparent() {
        await Assert.That(Trees.Dump("<p><a href=\"https://l.example/AI-1\">AI-1</a></p>")).IsEqualTo("doc(p(link[https://l.example/AI-1]('AI-1')))");
        await Assert.That(Trees.Dump("<div>&#x2705; a <code>x</code></div>\n<div>b</div>")).IsEqualTo("doc(p('✅ a ',code('x')),p('b'))");
        await Assert.That(Trees.Dump("<div>\n\n**md**\n\n</div>")).IsEqualTo("doc(p(em*2('md')))");
    }

    [Test]
    public async Task A_definition_list_indents_its_descriptions() =>
        await Assert.That(Trees.Dump("<dl>\n<dt>Term</dt>\n<dd>\n\n- x\n\n</dd>\n</dl>")).IsEqualTo("doc(p('Term'),indent(list(li(p('x')))))");

    [Test]
    public async Task Lists_nest_and_an_ordered_list_keeps_its_start() {
        await Assert.That(Trees.Dump("<ul>\n<li>a</li>\n<li>b<ul><li>c</li></ul></li>\n</ul>")).IsEqualTo("doc(list(li(p('a')),li(p('b'),list(li(p('c'))))))");
        var ordered = Trees.Parse("<ol start=\"3\">\n<li>x</li>\n</ol>");
        await Assert.That(TreeDump.Of(ordered)).IsEqualTo("doc(olist(li(p('x'))))");
        await Assert.That(ordered.Descendants<ListBlock>().Single().OrderedStart).IsEqualTo("3");
    }

    /// Pins the two nesting rules and the fixpoint between them: the stray paragraph rejects the
    /// list, which leaves the item at the top of the document, where an item cannot be.
    [Test]
    public async Task Stray_content_in_a_list_rejects_the_list_and_then_the_orphaned_item() {
        await Untouched("<ul>\ntext\n<li>a</li>\n</ul>");
        await Assert.That(Trees.Dump("<ul>\n\nstray\n\n<li>a</li>\n\n</ul>")).IsEqualTo("doc(html('<ul>'),p('stray'),html('<li>a</li>'),html('</ul>'))");
        await Untouched("<li>x</li>");
    }

    [Test]
    public async Task A_block_quote_folds_across_blocks_and_inside_details() {
        await Assert.That(Trees.Dump("<blockquote>\n\n**md**\n\n</blockquote>")).IsEqualTo("doc(quote(p(em*2('md'))))");
        await Assert.That(Trees.Dump("<details>\n<summary>S</summary><blockquote>\n\nbody\n\n</blockquote></details>")).IsEqualTo("doc(details#0{'S'}(quote(p('body'))))");
    }

    [Test]
    public async Task A_table_takes_its_widest_row_and_marks_header_rows() {
        const string table = "<table>\n<thead>\n<tr>\n<th>A</th>\n<th align=\"right\">B</th>\n</tr>\n</thead>\n<tbody>\n<tr><td colspan=\"2\"><b>x</b></td></tr>\n</tbody>\n</table>";
        var document = Trees.Parse(table);
        await Assert.That(TreeDump.Of(document)).IsEqualTo("doc(table2(thr(td(p('A')),td(p('B'))),tr(td2(p(em*2('x'))))))");
        var columns = document.Descendants<Table>().Single().ColumnDefinitions;
        await Assert.That(columns[0].Alignment).IsNull();
        await Assert.That(columns[1].Alignment).IsEqualTo(TableColumnAlign.Right);
        await Assert.That(Trees.Dump("<table><tr><th>A</th></tr></table>")).IsEqualTo("doc(table1(thr(td(p('A')))))");
    }

    [Test]
    public async Task Table_parts_out_of_place_stay_source() {
        await Untouched("<tr><td>x</td></tr>");
        await Assert.That(Trees.Dump("<table>\n\nstray\n\n</table>")).IsEqualTo("doc(html('<table>'),p('stray'),html('</table>'))");
    }

    [Test]
    public async Task A_rule_is_a_thematic_break_wherever_nothing_is_open() {
        await Assert.That(Trees.Dump("a\n\n<hr/>\n\nb")).IsEqualTo("doc(p('a'),hr,p('b'))");
        await Assert.That(Trees.Dump("<details>\n\nbody\n\n<hr/>\n\nmore\n\n</details>")).IsEqualTo("doc(details#0{}(p('body'),hr,p('more')))");
        await Untouched("<b>\na<hr>b\n</b>");
        await Assert.That(Trees.Dump("a <hr> b")).IsEqualTo("doc(p('a ',tag('<hr>'),' b'))");
    }

    /// A rule separates what is on either side of it; with nothing on one side, or another rule,
    /// it is dropped — in the document and inside any section.
    [Test]
    public async Task A_rule_with_nothing_on_one_side_is_dropped() {
        await Assert.That(Trees.Dump("<hr/>")).IsEqualTo("doc()");
        await Assert.That(Trees.Dump("a\n\n---\n\n---\n\nb\n\n---")).IsEqualTo("doc(p('a'),hr,p('b'))");
        await Assert.That(Trees.Dump("---\n\na")).IsEqualTo("doc(p('a'))");
        await Assert.That(Trees.Dump("<details>\n\nbody\n\n<hr/>\n</details>\n\nafter")).IsEqualTo("doc(details#0{}(p('body')),p('after'))");
    }

    /// A quote around nothing but details sections is how a bot indents them; the sections are
    /// set in by themselves, so the quote goes. One with any other content keeps its rail.
    [Test]
    public async Task A_quote_holding_only_details_sections_is_unwrapped() {
        await Assert.That(Trees.Dump("> <details>\n> <summary>A</summary>\n>\n> a\n>\n> </details>\n> <details>\n> <summary>B</summary>\n>\n> b\n>\n> </details>"))
            .IsEqualTo("doc(details#0{'A'}(p('a')),details#1{'B'}(p('b')))");
        await Assert.That(Trees.Dump("> note\n>\n> <details>\n> <summary>A</summary>\n>\n> a\n>\n> </details>"))
            .IsEqualTo("doc(quote(p('note'),details#0{'A'}(p('a'))))");
    }

    /// A `dl`/`dd` per level of sections is one bot's indentation, and a wrapper left holding
    /// only sections once its own wrapper is gone is unwrapped in the same pass.
    [Test]
    public async Task An_indent_holding_only_details_sections_is_unwrapped() {
        await Assert.That(Trees.Dump("<details>\n<summary>Files</summary>\n\n<dl>\n<dd>\n\n<details>\n<summary>A</summary>\n\na\n\n</details>\n\n</dd>\n</dl>\n\n</details>"))
            .IsEqualTo("doc(details#0{'Files'}(details#1{'A'}(p('a'))))");
        await Assert.That(Trees.Dump("<dl>\n<dd>\n\n> <details>\n> <summary>A</summary>\n>\n> a\n>\n> </details>\n\n</dd>\n</dl>"))
            .IsEqualTo("doc(details#0{'A'}(p('a')))");
        await Assert.That(Trees.Dump("<dl>\n<dd>\n\nnote\n\n<details>\n<summary>A</summary>\n\na\n\n</details>\n\n</dd>\n</dl>"))
            .IsEqualTo("doc(indent(p('note'),details#0{'A'}(p('a'))))");
    }

    [Test]
    public async Task A_close_tag_that_does_not_match_the_open_element_leaves_everything_source() =>
        await Assert.That(Trees.Dump("<div>\n\n<p>text\n\n</div>")).IsEqualTo("doc(html('<div>'),html('<p>text'),html('</div>'))");

    [Test]
    public async Task A_structural_tag_inside_formatting_rejects_the_block() => await Untouched("<b>\n<div>x</div>\n</b>");

    /// Formatting tags alone on their lines wrap the markdown between them and contribute nothing
    /// else; an anchor there would lose its target, so it stays source.
    [Test]
    public async Task Formatting_tags_alone_on_a_line_wrap_the_blocks_between() {
        await Assert.That(Trees.Dump("<sub>\n\n[l](https://u.example)\n\n</sub>")).IsEqualTo("doc(p(link[https://u.example]('l')))");
        await Assert.That(Trees.Dump("<sub>\n<b>\n\ntext\n\n</b>\n</sub>")).IsEqualTo("doc(p('text'))");
        await Assert.That(Trees.Dump("<a href=\"https://u.example\">\n\ntext\n\n</a>")).IsEqualTo("doc(html('<a href=\"https://u.example\">'),p('text'),html('</a>'))");
    }

    [Test]
    public async Task Transparent_inline_tags_contribute_their_content() {
        await Assert.That(Trees.Dump("p <span class=\"x\">a</span> <relative-time datetime=\"d\">t</relative-time>")).IsEqualTo("doc(p('p ','a',' ','t'))");
        await Assert.That(Trees.Dump("<picture><source srcset=\"https://h/d.svg\"><img src=\"https://h/l.svg\" alt=\"L\"></picture>")).IsEqualTo("doc(p(img[https://h/l.svg]('L')))");
        await Assert.That(Trees.Dump("p <small>x</small> <mark>y</mark> <u>z</u>")).IsEqualTo("doc(p('p ','x',' ',em=2('y'),' ',em+2('z')))");
        await Assert.That(Trees.Dump("<b>\n<span>a</span> <picture><img src=\"https://h/l.svg\" alt=\"L\"></picture>\n</b>")).IsEqualTo("doc(p(em*2('a',' '),img[https://h/l.svg](em*2('L'))))");
    }

    /// A break at the end of a block shows no line in a browser, and a block of breaks alone is a
    /// gap the block spacing already provides.
    [Test]
    public async Task Trailing_and_lone_line_breaks_are_dropped() {
        await Assert.That(Trees.Dump("<div>a<br/></div>")).IsEqualTo("doc(p('a'))");
        await Assert.That(Trees.Dump("<div><br/>a</div>")).IsEqualTo("doc(p(br,'a'))");
        await Assert.That(Trees.Dump("<div><br/></div>")).IsEqualTo("doc()");
        await Assert.That(Trees.Dump("<br/>")).IsEqualTo("doc()");
        await Assert.That(Trees.Dump("<b>\n<br>\n</b>")).IsEqualTo("doc()");
    }

    /// Pins the fold's depth budget for a synthesized container other than details.
    [Test]
    public async Task A_block_quote_fold_that_would_pass_the_depth_limit_is_refused() {
        static string Fixture(int levels) => string.Join('\n', new[] { "<blockquote>", "", "x", "", "</blockquote>" }.Select(line => NormalisationTests.Quoted(levels, line)));
        var accepted = Trees.Parse(Fixture(96));
        await Assert.That(TreeDump.Of(accepted)).Contains("quote(p('x'))");
        await Assert.That(Trees.MaxDepth(accepted)).IsEqualTo(GitHubHtmlPass.MaxDepth);
        var refused = Trees.Parse(Fixture(97));
        await Assert.That(TreeDump.Of(refused)).Contains("html('<blockquote>'),p('x'),html('</blockquote>')");
        await Assert.That(Trees.MaxDepth(refused)).IsEqualTo(GitHubHtmlPass.MaxDepth);
    }

    /// The shape of a Qodo review: a finding's details around quoted nested details, closed by a
    /// block that also holds a trailing rule, and a context section of divs and comments.
    [Test]
    public async Task A_qodo_review_converts_whole() {
        const string review =
            "<h3>Code Review by Qodo</h3>\n\n" +
            "<details>\n<summary>  1.  <s>Title</s> <code>✓ Resolved</code></summary>\n\n<br/>\n\n" +
            "> <details open>\n><summary>Description</summary>\n><br/>\n>\n><pre>\n>Text <b><i>Name</i></b>\n></pre>\n></details>\n\n" +
            "<hr/>\n</details>\n\n" +
            "<!-- qodo-context:start -->\n<details><summary><strong>Context sources</strong></summary>\n\n" +
            "<div>&#x2705; Rules: <a href=\"https://q.example/r?a=1&amp;b=2\"><code>64 rules</code></a></div>\n<div>Mode: <code>⚖️ Balanced</code></div>\n<!-- qodo-context:end -->\n</details>";
        await Assert.That(Trees.Dump(review)).IsEqualTo(
            "doc(h3('Code Review by Qodo')," +
            "details#0{'1. ',em~2('Title'),' ',code('✓ Resolved')}(details+#1{'Description'}(pre('Text ',em*2(em*1('Name')))))," +
            "details#2{em*2('Context sources')}(p('✅ Rules: ',link[https://q.example/r?a=1&b=2](code('64 rules'))),p('Mode: ',code('⚖️ Balanced'))))");
    }

    [Test]
    public async Task A_qodo_summary_section_converts_whole() {
        const string section = "<details>\n<summary>AI Description</summary>\n\n<dl>\n<dd>\n<br/>\n\n><pre>\n>• Bound\n></pre>\n\n</dd>\n</dl>\n\n</details>";
        await Assert.That(Trees.Dump(section)).IsEqualTo("doc(details#0{'AI Description'}(indent(quote(pre('• Bound')))))");
    }

    /// Dependabot writes a whole release-notes section as one HTML block, blockquote, headings
    /// and list included; the link inside the emphasis is hoisted as anywhere else.
    [Test]
    public async Task A_dependabot_release_notes_block_converts_whole() {
        const string notes = "<details>\n<summary>Changelog</summary>\n<p><em>Sourced from <a href=\"https://c.example\">changelog</a>.</em></p>\n<blockquote>\n<h2>[13.2.0]</h2>\n<ul>\n<li><code>x</code> for y.</li>\n</ul>\n</blockquote>\n</details>\n<br />";
        await Assert.That(Trees.Shape(notes)).StartsWith("doc(html(");
        await Assert.That(Trees.Dump(notes)).IsEqualTo(
            "doc(details#0{'Changelog'}(p(em*1('Sourced from '),link[https://c.example](em*1('changelog')),em*1('.')),quote(h2('[13.2.0]'),list(li(p(code('x'),' for y.'))))))");
    }
}
