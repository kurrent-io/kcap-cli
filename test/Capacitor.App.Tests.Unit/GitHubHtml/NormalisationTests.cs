using Capacitor.App.GitHubHtml;

namespace Capacitor.App.Tests.Unit.GitHubHtml;

public class NormalisationTests {
    [Test]
    public async Task A_markdown_image_holds_its_label_as_one_literal() {
        await Assert.That(Trees.Dump("![two *bold* words](https://h/x.png)")).IsEqualTo("doc(p(img[https://h/x.png]('two bold words')))");
        await Assert.That(Trees.Dump("![](https://h/path/name.png)")).IsEqualTo("doc(p(img[https://h/path/name.png]('name.png')))");
    }

    [Test]
    public async Task A_commonmark_autolink_becomes_a_link_and_an_email_stays() {
        await Assert.That(Trees.Dump("<https://example.com>")).IsEqualTo("doc(p(link[https://example.com]('https://example.com')))");
        await Assert.That(Trees.Dump("<a@b.example>")).IsEqualTo("doc(p(auto(a@b.example)))");
    }

    [Test]
    public async Task A_bare_url_already_is_a_link() =>
        await Assert.That(Trees.Dump("see https://example.com/x now")).IsEqualTo("doc(p('see ',link[https://example.com/x]('https://example.com/x'),' now'))");

    /// Pins the refusal: nested block quotes put the paragraph's inlines at the depth limit in the
    /// parsed tree, where giving a leaf a child would pass it. One quote fewer and both normalise.
    [Test]
    public async Task Normalisation_is_refused_where_its_literal_would_pass_the_depth_limit() {
        var refused = Trees.Parse(Quoted(97, "<https://example.com> ![](https://h/name.png)"));
        await Assert.That(TreeDump.Of(refused)).Contains("auto(https://example.com)");
        await Assert.That(TreeDump.Of(refused)).Contains("img[https://h/name.png]()");
        await Assert.That(Trees.MaxDepth(refused)).IsEqualTo(GitHubHtmlPass.MaxDepth);

        var accepted = Trees.Parse(Quoted(96, "<https://example.com> ![](https://h/name.png)"));
        await Assert.That(TreeDump.Of(accepted)).Contains("link[https://example.com]('https://example.com')");
        await Assert.That(TreeDump.Of(accepted)).Contains("img[https://h/name.png]('name.png')");
        await Assert.That(Trees.MaxDepth(accepted)).IsEqualTo(GitHubHtmlPass.MaxDepth);
    }

    /// An image that already has children can always take its label: replacing them never deepens.
    [Test]
    public async Task An_image_with_alt_text_is_labelled_at_any_depth() =>
        await Assert.That(TreeDump.Of(Trees.Parse(Quoted(97, "![alt](https://h/name.png)")))).Contains("img[https://h/name.png]('alt')");

    [Test]
    public async Task The_pipeline_parses_alert_blocks() =>
        await Assert.That(Trees.Dump("> [!NOTE]\n> text")).StartsWith("doc(quote(");

    internal static string Quoted(int levels, string text) => string.Concat(Enumerable.Repeat("> ", levels)) + text;
}
