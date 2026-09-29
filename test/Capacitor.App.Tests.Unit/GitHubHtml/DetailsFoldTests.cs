using Capacitor.App.GitHubHtml;

namespace Capacitor.App.Tests.Unit.GitHubHtml;

public class DetailsFoldTests {
    const string BotComment = "<details>\n<summary><strong>Agent Prompt</strong></summary>\n\nSome *markdown* here.\n\n</details>";

    static int Count(string text, string piece) => text.Split(piece).Length - 1;

    [Test]
    public async Task Markdown_between_blank_lines_folds_into_the_details() {
        await Assert.That(Trees.Shape(BotComment)).IsEqualTo("doc(html('<details>\\n<summary><strong>Agent Prompt</strong></summary>'),p('Some ',em*1('markdown'),' here.'),html('</details>'))");
        await Assert.That(Trees.Dump(BotComment)).IsEqualTo("doc(details#0{em*2('Agent Prompt')}(p('Some ',em*1('markdown'),' here.')))");
    }

    /// Inside one block the content is HTML, not markdown, as on github.com.
    [Test]
    public async Task A_pair_inside_one_block_folds_too() =>
        await Assert.That(Trees.Dump("<details><summary>S</summary>text *x* <b>y</b></details>")).IsEqualTo("doc(details#0{'S'}(p('text *x* ',em*2('y'))))");

    [Test]
    public async Task Open_and_missing_summary_are_carried() {
        await Assert.That(Trees.Dump("<details open>\n<summary>S</summary>\n\nbody\n\n</details>")).IsEqualTo("doc(details+#0{'S'}(p('body')))");
        await Assert.That(Trees.Dump("<details>\n\nbody\n\n</details>")).IsEqualTo("doc(details#0{}(p('body')))");
    }

    [Test]
    public async Task Content_outside_the_pair_stays_outside() =>
        await Assert.That(Trees.Dump("<details><summary>S</summary></details>after")).IsEqualTo("doc(details#0{'S'}(),p('after'))");

    [Test]
    public async Task Pairs_nest_and_ordinals_follow_document_order() {
        const string nested = "<details>\n<summary>Outer</summary>\n\n<details>\n<summary>Inner</summary>\n\ninner body\n\n</details>\n\nouter tail\n\n</details>";
        await Assert.That(Trees.Dump(nested)).IsEqualTo("doc(details#0{'Outer'}(details#1{'Inner'}(p('inner body')),p('outer tail')))");

        const string siblings = "<details>\n\na\n\n</details>\n\n<details>\n\n<details>\n\nc\n\n</details>\n\n</details>";
        await Assert.That(Trees.Dump(siblings)).IsEqualTo("doc(details#0{}(p('a')),details#1{}(details#2{}(p('c'))))");
    }

    /// Pins rejection propagation: the inner opener is rejected for its unknown iframe tag, which
    /// rejects its partner closer with it, and the outer pair folds around both.
    [Test]
    public async Task A_rejected_opener_takes_its_closer_with_it_and_the_outer_pair_still_folds() {
        const string markdown = "<details><summary>Outer</summary>\n\n<details><iframe>unsupported</iframe>\n\ninner body\n\n</details>\n\nouter tail\n\n</details>";
        await Assert.That(Trees.Dump(markdown))
            .IsEqualTo("doc(details#0{'Outer'}(html('<details><iframe>unsupported</iframe>'),p('inner body'),html('</details>'),p('outer tail')))");
    }

    [Test]
    public async Task An_unmatched_tag_leaves_its_block_as_source() {
        await Assert.That(Trees.Dump("<details>\n<summary>S</summary>\n\nbody")).IsEqualTo("doc(html('<details>\\n<summary>S</summary>'),p('body'))");
        await Assert.That(Trees.Dump("body\n\n</details>")).IsEqualTo("doc(p('body'),html('</details>'))");
    }

    /// Matching happens within one parent: a pair split between a list item and the document is
    /// two unmatched tags.
    [Test]
    public async Task A_pair_is_matched_within_one_container_only() {
        const string split = "- <details>\n  <summary>S</summary>\n\n  item body\n\n</details>";
        await Assert.That(Trees.Dump(split)).IsEqualTo("doc(list(li(html('<details>\\n<summary>S</summary>'),p('item body'))),html('</details>'))");

        const string inItem = "- <details>\n  <summary>S</summary>\n\n  body\n\n  </details>";
        await Assert.That(Trees.Dump(inItem)).IsEqualTo("doc(list(li(details#0{'S'}(p('body')))))");

        const string inQuote = "> note\n>\n> <details>\n> <summary>S</summary>\n>\n> body\n>\n> </details>";
        await Assert.That(Trees.Dump(inQuote)).IsEqualTo("doc(quote(p('note'),details#0{'S'}(p('body'))))");
    }

    [Test]
    public async Task A_second_summary_rejects_its_block_and_the_pair() =>
        await Assert.That(Trees.Dump("<details>\n<summary>A</summary>\n<summary>B</summary>\n\n</details>"))
            .IsEqualTo("doc(html('<details>\\n<summary>A</summary>\\n<summary>B</summary>'),html('</details>'))");

    /// A block that closes one pair and opens another that never closes is rejected whole, and
    /// that rejection reaches the pair it closed.
    [Test]
    public async Task A_block_with_an_unmatched_opener_rejects_the_pair_it_closes() =>
        await Assert.That(Trees.Dump("<details>\n\na\n\n</details><details>\n\nb"))
            .IsEqualTo("doc(html('<details>'),p('a'),html('</details><details>'),p('b'))");

    [Test]
    public async Task A_summary_holds_no_links() =>
        await Assert.That(Trees.Dump("<details>\n<summary><a href=\"https://u.example\">L</a> <img src=\"https://h/i.png\"></summary>\n\nb\n\n</details>"))
            .IsEqualTo("doc(details#0{'L',' ','i.png'}(p('b')))");

    [Test]
    public async Task A_pre_directly_inside_the_pair_is_accepted() =>
        await Assert.That(Trees.Dump("<details><summary>S</summary><pre>a\nb</pre></details>")).IsEqualTo("doc(details#0{'S'}(pre('a',br,'b')))");

    [Test]
    public async Task A_lone_closer_block_passes_the_block_rule_and_folds() =>
        await Assert.That(Trees.Dump("<details>\n\n- one\n- two\n\n</details>")).IsEqualTo("doc(details#0{}(list(li(p('one')),li(p('two')))))");

    /// The ninth level is not folded; the eight around it are.
    [Test]
    public async Task Nesting_deeper_than_eight_stays_source() {
        var markdown = string.Concat(Enumerable.Repeat("<details>\n\n", 9)) + "x\n\n" + string.Concat(Enumerable.Repeat("</details>\n\n", 9));
        var dump = Trees.Dump(markdown);
        await Assert.That(Count(dump, "details#")).IsEqualTo(8);
        await Assert.That(dump).Contains("html('<details>'),p('x'),html('</details>')");
    }

    /// Pins the fold's depth budget: the details node sits one level below its container, its
    /// paragraph two, that paragraph's root three and the literal four.
    [Test]
    public async Task A_fold_that_would_pass_the_depth_limit_is_refused() {
        static string Quoted(int levels, string line) => string.Concat(Enumerable.Repeat("> ", levels)) + line;
        static string Fixture(int levels) => string.Join('\n', [Quoted(levels, "y"), Quoted(levels, ""), Quoted(levels, "<details>"), Quoted(levels, ""), Quoted(levels, "x"), Quoted(levels, ""), Quoted(levels, "</details>")]);

        var accepted = Trees.Parse(Fixture(96));
        await Assert.That(TreeDump.Of(accepted)).Contains("details#0{}(p('x'))");
        await Assert.That(Trees.MaxDepth(accepted)).IsEqualTo(GitHubHtmlPass.MaxDepth);

        var refused = Trees.Parse(Fixture(97));
        await Assert.That(TreeDump.Of(refused)).Contains("html('<details>'),p('x'),html('</details>')");
        await Assert.That(Trees.MaxDepth(refused)).IsEqualTo(GitHubHtmlPass.MaxDepth);
    }
}
