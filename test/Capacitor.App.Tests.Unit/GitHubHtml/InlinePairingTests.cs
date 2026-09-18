using Capacitor.App.GitHubHtml;

namespace Capacitor.App.Tests.Unit.GitHubHtml;

public class InlinePairingTests {
    static int Count(string text, string piece) => text.Split(piece).Length - 1;

    [Test]
    [Arguments("b", "em*2")]
    [Arguments("strong", "em*2")]
    [Arguments("i", "em*1")]
    [Arguments("em", "em*1")]
    [Arguments("del", "em~2")]
    [Arguments("s", "em~2")]
    [Arguments("strike", "em~2")]
    [Arguments("ins", "em+2")]
    [Arguments("sub", "em~1")]
    [Arguments("sup", "em^1")]
    public async Task A_formatting_pair_becomes_emphasis(string tag, string emphasis) =>
        await Assert.That(Trees.Dump($"p <{tag}>x</{tag}> q")).IsEqualTo($"doc(p('p ',{emphasis}('x'),' q'))");

    [Test]
    public async Task Tag_names_match_in_any_case() =>
        await Assert.That(Trees.Dump("p <B>x</b>")).IsEqualTo("doc(p('p ',em*2('x')))");

    /// A code-like tag is a leaf: what Markdig parsed inside it is flattened to its text.
    [Test]
    [Arguments("code")]
    [Arguments("kbd")]
    [Arguments("tt")]
    [Arguments("samp")]
    public async Task A_code_like_pair_becomes_inline_code_holding_plain_text(string tag) =>
        await Assert.That(Trees.Dump($"p <{tag}>a *b*\nc</{tag}>")).IsEqualTo("doc(p('p ',code('a b c')))");

    [Test]
    public async Task An_anchor_becomes_a_link_and_one_without_a_target_unwraps() {
        await Assert.That(Trees.Dump("p <a href=\"https://e.example/x\">go</a>")).IsEqualTo("doc(p('p ',link[https://e.example/x]('go')))");
        await Assert.That(Trees.Dump("p <a name=\"top\">go</a>")).IsEqualTo("doc(p('p ','go'))");
    }

    /// The pass maps the target as written; refusing it is the link policy's job at render time.
    [Test]
    public async Task A_script_target_is_mapped_for_the_policy_to_refuse() =>
        await Assert.That(Trees.Dump("p <a href=\"javascript:alert(1)\">x</a>")).IsEqualTo("doc(p('p ',link[javascript:alert(1)]('x')))");

    [Test]
    public async Task Void_tags_convert_on_their_own_with_or_without_a_slash() {
        await Assert.That(Trees.Dump("a<br>b<br/>c")).IsEqualTo("doc(p('a',br,'b',br,'c'))");
        await Assert.That(Trees.Dump("p <img src=\"https://h/y.png\" alt=\"shot\">")).IsEqualTo("doc(p('p ',img[https://h/y.png]('shot')))");
        await Assert.That(Trees.Dump("p <img src=\"https://h/y.png\" />")).IsEqualTo("doc(p('p ',img[https://h/y.png]('y.png')))");
    }

    [Test]
    public async Task A_close_tag_for_a_void_tag_stays_source() {
        await Assert.That(Trees.Dump("a</br>b")).IsEqualTo("doc(p('a',tag('</br>'),'b'))");
        await Assert.That(Trees.Dump("a</img>b")).IsEqualTo("doc(p('a',tag('</img>'),'b'))");
    }

    [Test]
    public async Task A_trailing_slash_on_a_paired_tag_is_ignored() {
        await Assert.That(Trees.Dump("p <b/>x</b>")).IsEqualTo("doc(p('p ',em*2('x')))");
        await Assert.That(Trees.Dump("p <b/>x")).IsEqualTo("doc(p('p ',tag('<b/>'),'x'))");
    }

    [Test]
    public async Task Pairs_nest() =>
        await Assert.That(Trees.Dump("p <b><i>x</i></b>")).IsEqualTo("doc(p('p ',em*2(em*1('x'))))");

    /// Pins the crossing rule: `&lt;/b&gt;` matches past the open `&lt;i&gt;`, which is then spent — it stays
    /// source inside the bold and can never pair with the `&lt;/i&gt;` that follows.
    [Test]
    public async Task Crossing_tags_never_produce_crossing_nodes() {
        await Assert.That(Trees.Dump("p <b><i>x</b>y</i>")).IsEqualTo("doc(p('p ',em*2(tag('<i>'),'x'),'y',tag('</i>')))");
        await Assert.That(Trees.Dump("p <i><b><i>x</b>y</i>")).IsEqualTo("doc(p('p ',em*1(em*2(tag('<i>'),'x'),'y')))");
    }

    [Test]
    public async Task A_pair_split_across_two_containers_stays_source() =>
        await Assert.That(Trees.Dump("**<b>x**</b>")).IsEqualTo("doc(p(em*2(tag('<b>'),'x'),tag('</b>')))");

    [Test]
    public async Task Unmatched_and_unknown_tags_stay_source() {
        await Assert.That(Trees.Dump("a <b>x")).IsEqualTo("doc(p('a ',tag('<b>'),'x'))");
        await Assert.That(Trees.Dump("a x</b>")).IsEqualTo("doc(p('a x',tag('</b>')))");
        await Assert.That(Trees.Dump("a <iframe>x</iframe>")).IsEqualTo("doc(p('a ',tag('<iframe>'),'x',tag('</iframe>')))");
    }

    /// Block-only tags that Markdig parsed mid-paragraph have no block to become.
    [Test]
    public async Task Block_only_tags_in_a_paragraph_stay_source() {
        await Assert.That(Trees.Dump("prefix <pre>x</pre> suffix")).IsEqualTo("doc(p('prefix ',tag('<pre>'),'x',tag('</pre>'),' suffix'))");
        await Assert.That(Trees.Dump("prefix <details><summary>S</summary>x</details> suffix"))
            .IsEqualTo("doc(p('prefix ',tag('<details>'),tag('<summary>'),'S',tag('</summary>'),'x',tag('</details>'),' suffix'))");
        await Assert.That(Trees.Dump("<b><pre>x</pre></b>")).IsEqualTo("doc(p(em*2(tag('<pre>'),'x',tag('</pre>'))))");
    }

    [Test]
    public async Task An_inline_comment_is_removed() =>
        await Assert.That(Trees.Dump("a <!-- c --> b")).IsEqualTo("doc(p('a ',' b'))");

    /// Pins the two fallbacks of the depth budget in a paragraph: pairs convert from the inside
    /// out until the next one would pass the limit, and the rest stay source tags. The root inline
    /// container is at depth 2, so a wrap may reach 98 levels: 97 emphasis nodes over a literal.
    [Test]
    public async Task Nested_pairs_convert_up_to_the_depth_limit() {
        var markdown = "p " + string.Concat(Enumerable.Repeat("<b>", 200)) + "x" + string.Concat(Enumerable.Repeat("</b>", 200));
        var document = Trees.Parse(markdown);
        var dump = TreeDump.Of(document);
        await Assert.That(Trees.MaxDepth(document)).IsEqualTo(GitHubHtmlPass.MaxDepth);
        await Assert.That(Count(dump, "em*2(")).IsEqualTo(97);
        await Assert.That(Count(dump, "tag('<b>')")).IsEqualTo(103);
        await Assert.That(Count(dump, "tag('</b>')")).IsEqualTo(103);
    }

    /// The centre is normalised while the wrappers are still sibling tags, so the budget sees a
    /// link holding a literal and converts one pair fewer than around plain text.
    [Test]
    [Arguments("<https://example.com>", "link[https://example.com]('https://example.com')")]
    [Arguments("![](https://h/name.png)", "img[https://h/name.png]('name.png')")]
    [Arguments("<img src=\"https://h/name.png\">", "img[https://h/name.png]('name.png')")]
    public async Task The_budget_counts_what_normalisation_added(string centre, string expected) {
        var markdown = "p " + string.Concat(Enumerable.Repeat("<b>", 200)) + centre + string.Concat(Enumerable.Repeat("</b>", 200));
        var document = Trees.Parse(markdown);
        var dump = TreeDump.Of(document);
        await Assert.That(dump).Contains(expected);
        await Assert.That(Trees.MaxDepth(document)).IsEqualTo(GitHubHtmlPass.MaxDepth);
        await Assert.That(Count(dump, "em*2(")).IsEqualTo(96);
    }

    /// An `img` tag is refused like a wrap: under enough block quotes its label would pass the
    /// limit, and the tag stays source. One quote fewer and it converts.
    [Test]
    public async Task An_img_tag_is_refused_where_its_label_would_pass_the_depth_limit() {
        await Assert.That(TreeDump.Of(Trees.Parse(NormalisationTests.Quoted(97, "p <img src=\"https://h/name.png\">")))).Contains("tag('<img src=\"https://h/name.png\">')");
        await Assert.That(TreeDump.Of(Trees.Parse(NormalisationTests.Quoted(96, "p <img src=\"https://h/name.png\">")))).Contains("img[https://h/name.png]('name.png')");
    }

    /// Pins linear work: every close tag here looks for an open tag of a name that is not on the
    /// stack, past a hundred thousand that are. A search of the stack would be ten billion steps.
    [Test]
    [Timeout(30_000)]
    public async Task Many_unmatched_tags_stay_linear(CancellationToken _) {
        var markdown = "p " + string.Concat(Enumerable.Repeat("<i>", 100_000)) + string.Concat(Enumerable.Repeat("</b>", 100_000));
        var document = Trees.Parse(markdown);
        await Assert.That(Trees.MaxDepth(document)).IsEqualTo(3);
    }
}
