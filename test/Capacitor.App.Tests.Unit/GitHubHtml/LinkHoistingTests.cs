namespace Capacitor.App.Tests.Unit.GitHubHtml;

public class LinkHoistingTests {
    static string Wrapped(int levels, string centre) =>
        "p " + string.Concat(Enumerable.Repeat("<b>", levels)) + centre + string.Concat(Enumerable.Repeat("</b>", levels));

    [Test]
    public async Task A_link_rises_above_its_emphasis_and_the_emphasis_splits_around_it() =>
        await Assert.That(Trees.Dump("**a [b](https://u.example) c**"))
            .IsEqualTo("doc(p(em*2('a '),link[https://u.example](em*2('b')),em*2(' c')))");

    [Test]
    public async Task An_only_child_leaves_no_empty_copies() =>
        await Assert.That(Trees.Dump("**[b](https://u.example)**")).IsEqualTo("doc(p(link[https://u.example](em*2('b'))))");

    /// Pins the order of the copies: the outer emphasis stays outermost inside the link.
    [Test]
    public async Task Nested_formatting_keeps_its_order_and_delimiters_inside_the_link() {
        var document = Trees.Parse("p <b><i><a href=\"https://u.example\">x</a></i></b>");
        await Assert.That(TreeDump.Of(document)).IsEqualTo("doc(p('p ',link[https://u.example](em*2(em*1('x')))))");
        await Assert.That(Trees.MaxDepth(document)).IsEqualTo(6);
    }

    [Test]
    public async Task An_emphasised_image_keeps_its_emphasis_around_its_label() {
        await Assert.That(Trees.Dump("**![badge](https://h/b.png)**")).IsEqualTo("doc(p(img[https://h/b.png](em*2('badge'))))");
        await Assert.That(Trees.Dump("**![](https://h/name.png)**")).IsEqualTo("doc(p(img[https://h/name.png](em*2('name.png'))))");
    }

    [Test]
    public async Task An_emphasised_autolink_is_hoisted_like_any_link() {
        await Assert.That(Trees.Dump("**<https://example.com>**")).IsEqualTo("doc(p(link[https://example.com](em*2('https://example.com'))))");
        await Assert.That(Trees.Dump("p <b><https://example.com></b>")).IsEqualTo("doc(p('p ',link[https://example.com](em*2('https://example.com'))))");
    }

    /// A link inside a link rises to the outer link's children and no further: which of the two
    /// becomes the hyperlink is the renderer's decision.
    [Test]
    public async Task A_link_inside_a_link_stops_below_the_outer_link() =>
        await Assert.That(Trees.Dump("p <a href=\"https://outer.example\">a <b><a href=\"https://inner.example\">b</a></b> c</a>"))
            .IsEqualTo("doc(p('p ',link[https://outer.example]('a ',link[https://inner.example](em*2('b')),' c')))");

    [Test]
    public async Task A_link_under_more_than_eight_emphasis_ancestors_stays_put() {
        await Assert.That(Trees.Dump(Wrapped(8, "<a href=\"https://u.example\">x</a>"))).StartsWith("doc(p('p ',link[https://u.example](");
        await Assert.That(Trees.Dump(Wrapped(9, "<a href=\"https://u.example\">x</a>"))).Contains("em*2(link[https://u.example]('x'))");
    }

    /// A childless image is one whose label the depth budget refused; its renderer writes the
    /// label, which inherits the emphasis only while the image stays inside it.
    [Test]
    public async Task A_childless_image_is_never_hoisted() =>
        await Assert.That(TreeDump.Of(Trees.Parse(NormalisationTests.Quoted(96, "**![](https://h/name.png)**")))).Contains("em*2(img[https://h/name.png]())");

    /// Pins the bound on what one comment can make the pass create: a hoist copies its chain at
    /// most twice, so 2 000 links under 8 levels add no more than 16 emphasis nodes each.
    [Test]
    [Timeout(30_000)]
    public async Task Hoisting_many_sibling_links_stays_bounded(CancellationToken _) {
        var links = string.Concat(Enumerable.Range(0, 2_000).Select(n => $"<a href=\"https://u.example/{n}\">x</a> "));
        var dump = Trees.Dump(Wrapped(8, links));
        await Assert.That(dump.Split("link[").Length - 1).IsEqualTo(2_000);
        await Assert.That(dump.Split("em*2(").Length - 1).IsLessThanOrEqualTo(8 + 16 * 2_000);
        await Assert.That(dump).DoesNotContain("em*2(link[");
    }
}
