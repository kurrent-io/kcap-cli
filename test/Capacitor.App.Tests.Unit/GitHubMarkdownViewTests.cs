using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;
using Capacitor.App.Views;
using MarkView.Avalonia.Rendering;
using MarkView.Avalonia.Rendering.Inlines;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.MarkdownViewHarness;

namespace Capacitor.App.Tests.Unit;

public class GitHubMarkdownViewTests {
    const string BotComment =
        "<img src=\"https://img.shields.io/badge/Medium-634FD1\" height=\"20px\" alt=\"Remediation recommended\">\n\n" +
        "1. The title breaks casing <code>📘 Rule violation</code>\n\n" +
        "<pre>\nThe supplied title <b><i>Name the repo&#x27;s projects</i></b> begins its\nimperative clause with a capital.\n</pre>";

    static string Quoted(int levels, string text) => string.Concat(Enumerable.Repeat("> ", levels)) + text;

    /// Pins the headline: the bot comment renders with no literal tag anywhere, the badge as an
    /// image labelled by its alt text and sized by its tag, the emphasis inside the pre, and the
    /// code span.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_bot_comment_renders_with_no_literal_tags() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show(BotComment, MarkdownFlavor.GitHub);
            try {
                await Assert.That(All<TextBlock>(root).Select(Reads).Any(text => text.Contains('<'))).IsFalse();
                var badge = Images(root).Single();
                await Assert.That(badge.Url).IsEqualTo("https://img.shields.io/badge/Medium-634FD1");
                await Assert.That(badge.Target).IsNull();
                await Assert.That(badge.Label).IsEqualTo("Remediation recommended");
                await Assert.That(badge.ShowsPicture).IsFalse();
                await Assert.That(All<Image>(badge).Single().Height).IsEqualTo(20);
                await Assert.That(AllLinks(root)).IsEmpty();
                await Assert.That(Runs(root).Any(r => r.Text == "📘 Rule violation" && r.Classes.Contains("markdown-code-inline"))).IsTrue();

                var pre = All<Border>(root).Single(b => b.Classes.Contains("markdown-pre"));
                await Assert.That(pre.Classes.Contains("markdown-code-block")).IsTrue();
                var text = All<MarkdownSelectableTextBlock>(pre).Single();
                var bold = Spans<Bold>(text.Inlines!).Single();
                await Assert.That(Spans<Italic>(bold.Inlines).Single().Inlines.Text).IsEqualTo("Name the repo's projects");
                await Assert.That(text.Inlines!.OfType<LineBreak>().Count()).IsEqualTo(1);
            } finally { window.Close(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_chat_flavor_still_shows_the_same_comment_as_source() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show(BotComment);
            try {
                var texts = All<TextBlock>(root).Select(Reads).ToList();
                await Assert.That(texts.Any(t => t.Contains("<img src="))).IsTrue();
                await Assert.That(texts.Any(t => t.Contains("<pre>"))).IsTrue();
                await Assert.That(texts.Any(t => t.Contains("<code>📘 Rule violation</code>"))).IsTrue();
                await Assert.That(AllLinks(root)).IsEmpty();
            } finally { window.Close(); }
        });
    }

    /// An image inside an openable anchor opens that anchor, whatever the anchor's markdown or
    /// HTML spelling and whatever emphasis sits between. Until its bytes arrive it shows its alt
    /// text, or its file name when the tag has none.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments("[![badge](https://h/b.png)](https://outer.example)", "https://outer.example", "badge")]
    [Arguments("<a href=\"https://outer.example\"><img src=\"https://h/b.png\" alt=\"badge\"></a>", "https://outer.example", "badge")]
    [Arguments("**[![badge](https://h/b.png)](https://outer.example)**", "https://outer.example", "badge")]
    public async Task A_linked_image_opens_its_anchor_through_the_command(string markdown, string target, string label) {
        await RunOnUiAsync(async () => {
            var (window, root, opened) = Show(markdown, MarkdownFlavor.GitHub);
            try {
                var image = Images(root).Single();
                await Assert.That(image.Label).IsEqualTo(label);
                await Assert.That(image.Target).IsEqualTo(target);
                await Assert.That(image.ShowsPicture).IsFalse();
                await Assert.That(All<TextBlock>(root).Select(Reads)).Contains(label);
                ClickCentre(window, image);
                await Assert.That(opened).IsEquivalentTo(new[] { target });
            } finally { window.Close(); }
        });
    }

    /// A bare image, or one whose only anchor the policy refuses, has no Target and a press opens
    /// nothing.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments("See ![the shot](https://h/x.png) here.", "the shot")]
    [Arguments("**![badge](https://h/b.png)**", "badge")]
    [Arguments("**![](https://h/name.png)**", "name.png")]
    [Arguments("p <a href=\"javascript:x\"><img src=\"https://h/b.png\" alt=\"badge\"></a>", "badge")]
    [Arguments("<img src=\"https://www.qodo.ai/wp-content/uploads/2025/11/light-grey-line.svg\" alt=\"Grey Divider\">", "Grey Divider")]
    public async Task A_bare_image_is_not_a_link(string markdown, string label) {
        await RunOnUiAsync(async () => {
            var (window, root, opened) = Show(markdown, MarkdownFlavor.GitHub);
            try {
                var image = Images(root).Single();
                await Assert.That(image.Label).IsEqualTo(label);
                await Assert.That(image.Target).IsNull();
                await Assert.That(All<TextBlock>(root).Select(Reads)).Contains(label);
                ClickCentre(window, image);
                await Assert.That(opened).IsEmpty();
            } finally { window.Close(); }
        });
    }

    /// The bytes decode to a picture that replaces the label; an SVG — what every badge service
    /// serves — is recognised by its text. A picture may not pass the pane's edge, including one
    /// whose tag asked for a width larger than the pane; a height-only tag keeps that height.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_image_shows_its_picture_once_its_bytes_arrive() {
        var png = new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };
        var svg = System.Text.Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"2000\" height=\"20\"><rect width=\"2000\" height=\"20\"/></svg>");
        await RunOnUiAsync(() => WithImageBytes(png, async () => {
            var (window, root, _) = Show("![shot](https://h/x.png) <img src=\"https://h/badge\" alt=\"b\" height=\"20px\">", MarkdownFlavor.GitHub);
            try {
                await Settled(window);
                var images = Images(root).ToList();
                await Assert.That(images.Select(i => i.ShowsPicture)).IsEquivalentTo([true, true]);
                await Assert.That(All<TextBlock>(root).Where(t => t.IsVisible).Select(Reads).Any(t => t.Contains("shot"))).IsFalse();
                await Assert.That(All<Image>(images[1]).Single().Height).IsEqualTo(20);
            } finally { window.Close(); }
        }));
        await RunOnUiAsync(() => WithImageBytes(svg, async () => {
            var (window, root, _) = Show("![wide](https://h/divider)", MarkdownFlavor.GitHub);
            try {
                await Settled(window);
                var picture = All<Image>(Images(root).Single()).Single();
                await Assert.That(picture.Source).IsTypeOf<Avalonia.Svg.Skia.SvgImage>();
                await Assert.That(picture.Bounds.Width).IsLessThanOrEqualTo(root.Bounds.Width);
                await Assert.That(picture.Bounds.Width).IsGreaterThan(100);
            } finally { window.Close(); }
        }));
        await RunOnUiAsync(() => WithImageBytes(svg, async () => {
            var (window, root, _) = Show("<img src=\"https://h/wide\" alt=\"shot\" width=\"2000\" height=\"400\">", MarkdownFlavor.GitHub, width: 320);
            try {
                await Settled(window);
                var image = Images(root).Single();
                var picture = All<Image>(image).Single();
                await Assert.That(image.ShowsPicture).IsTrue();
                await Assert.That(picture.Bounds.Width).IsLessThanOrEqualTo(root.Bounds.Width);
                await Assert.That(picture.Bounds.Width).IsLessThan(2000);
            } finally { window.Close(); }
        }));
    }

    /// A paragraph of nothing but images is a row of block images at their natural size; an
    /// image beside text is embedded in the line, capped to fit its exact height.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Images_alone_in_a_paragraph_form_a_row_and_beside_text_stay_inline() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show("[![a](https://h/a.png)](https://outer.example) ![b](https://h/b.png)\n\nSee ![c](https://h/c.png) here.", MarkdownFlavor.GitHub);
            try {
                var row = All<WrapPanel>(root).Single(p => p.Classes.Contains("markdown-image-row"));
                var block = row.Children.OfType<MarkdownImage>().ToList();
                await Assert.That(block.Select(i => i.Label)).IsEquivalentTo(["a", "b"]);
                await Assert.That(block[0].Target).IsEqualTo("https://outer.example");
                await Assert.That(block[1].Target).IsNull();
                await Assert.That(block.All(i => !i.IsInline)).IsTrue();
                var inline = Images(root).Single(i => i.Label == "c");
                await Assert.That(inline.IsInline).IsTrue();
                await Assert.That(inline.Target).IsNull();
                await Assert.That(All<Image>(inline).Single().MaxHeight).IsEqualTo(MarkdownImage.InlineHeight);
                var paragraph = Paragraphs(root).Single(p => Reads(p).Contains("See"));
                await Assert.That(paragraph.Inlines!.OfType<InlineUIContainer>().Single().Child).IsSameReferenceAs(inline);
                await Assert.That(Paragraphs(root).Count()).IsEqualTo(1);
                Viewer(root).SelectAll();
                await Assert.That(Viewer(root).GetSelectedText()).Contains("here");
            } finally { window.Close(); }
        });
    }

    static async Task Settled(Window window) {
        for (var i = 0; i < 20 && Images(window).Any(image => !image.ShowsPicture); i++) {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    /// Pins one hyperlink at a time, on ancestry and on the URL dispatched: the outermost openable
    /// link wins, and a refused outer link lets the inner one through.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments("p <a href=\"https://outer.example\">[inner](https://inner.example)</a>", "https://outer.example/", "inner")]
    [Arguments("p <a href=\"https://outer.example\">a <a href=\"https://inner.example\">b</a></a>", "https://outer.example/", "a b")]
    [Arguments("p <a href=\"javascript:x\">[inner](https://inner.example)</a>", "https://inner.example/", "inner")]
    public async Task Only_one_hyperlink_at_a_time(string markdown, string expected, string label) {
        await RunOnUiAsync(async () => {
            var (window, root, opened) = Show(markdown, MarkdownFlavor.GitHub);
            try {
                var (block, link) = AllLinks(root).Single();
                await Assert.That(link.NavigateUri?.ToString()).IsEqualTo(expected);
                await Assert.That(link.Inlines.Text).IsEqualTo(label);
                await Assert.That(Spans<MarkdownHyperlink>(link.Inlines)).IsEmpty();
                Click(window, block, link);
                await Assert.That(opened).IsEquivalentTo(new[] { expected });
            } finally { window.Close(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_chat_flavor_keeps_a_linked_image_as_source_text_inside_the_link() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show("[![badge](https://h/b.png)](https://outer.example)");
            try {
                var link = Links(root).Single();
                await Assert.That(link.NavigateUri?.ToString()).IsEqualTo("https://outer.example/");
                await Assert.That(link.Inlines.Text).IsEqualTo("![badge](https://h/b.png)");
            } finally { window.Close(); }
        });
    }

    /// Every link here sits under formatting, inside a pre, after an emoji or in a list item —
    /// and each is a direct inline of a selectable text block that opens exactly once.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments("**[x](https://u.example/1)**", "https://u.example/1")]
    [Arguments("p <b><i><a href=\"https://u.example/2\">y</a></i></b>", "https://u.example/2")]
    [Arguments("**<https://example.com>**", "https://example.com/")]
    [Arguments("<pre><a href=\"https://u.example/pre\">first line</a>\nsecond</pre>", "https://u.example/pre")]
    [Arguments("🚀 shipped [go](https://u.example/e) now", "https://u.example/e")]
    [Arguments("- [item](https://u.example/li)", "https://u.example/li")]
    public async Task A_link_anywhere_is_a_direct_inline_that_opens_once(string markdown, string expected) {
        await RunOnUiAsync(async () => {
            var (window, root, opened) = Show(markdown, MarkdownFlavor.GitHub);
            try {
                var (block, link) = AllLinks(root).Single();
                await Assert.That(block).IsTypeOf<MarkdownSelectableTextBlock>();
                await Assert.That(block.Inlines!.Contains(link)).IsTrue();
                Click(window, block, link);
                await Assert.That(opened).IsEquivalentTo(new[] { expected });
            } finally { window.Close(); }
        });
    }

    /// MarkView measures a line break as `Environment.NewLine`, and Avalonia lays it out as one
    /// character; where the two differ, a link after a line break is the library's defect, not this
    /// flavor's, so the test only speaks where they agree.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_link_after_a_line_break_inside_pre_opens() {
        if (Environment.NewLine.Length != 1) Skip.Test("MarkView measures a line break as Environment.NewLine, Avalonia lays it out as one character; they disagree here.");
        await RunOnUiAsync(async () => {
            var (window, root, opened) = Show("<pre>first\n<a href=\"https://u.example/second\">second</a></pre>", MarkdownFlavor.GitHub);
            try {
                var (block, link) = AllLinks(root).Single();
                Click(window, block, link);
                await Assert.That(opened).IsEquivalentTo(new[] { "https://u.example/second" });
            } finally { window.Close(); }
        });
    }

    /// Pins the source fallback's layout guard in both flavors: a tag that spans lines lays out
    /// under a height-unconstrained parent, no run holds a line end, and the text still reads as
    /// the source.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Timeout(30_000)]
    public async Task A_multi_line_source_tag_lays_out_and_still_reads_as_source(CancellationToken _) {
        await RunOnUiAsync(async () => {
            foreach (var flavor in new[] { MarkdownFlavor.Chat, MarkdownFlavor.GitHub }) {
                var view = new MarkdownView { Flavor = flavor, Text = "a <iframe\ntitle=\"x\">b</iframe> c\n\nd <b\nclass=\"x\">z", Width = 400 };
                var window = new Window { Content = new ScrollViewer { Content = new StackPanel { Children = { view } } }, Width = 500, Height = 400 };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                try {
                    await Assert.That(view.Bounds.Height).IsGreaterThan(0);
                    await Assert.That(Runs(view).Any(r => r.Text!.Contains('\n') || r.Text!.Contains('\r'))).IsFalse();
                    var texts = Paragraphs(view).Select(p => Reads(p).ReplaceLineEndings("\n")).ToList();
                    await Assert.That(texts).Contains("a <iframe\ntitle=\"x\">b</iframe> c");
                    await Assert.That(texts).Contains("d <b\nclass=\"x\">z");
                } finally { window.Close(); }
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Timeout(30_000)]
    public async Task An_image_whose_file_name_unescapes_to_a_line_end_lays_out(CancellationToken _) {
        await RunOnUiAsync(async () => {
            var view = new MarkdownView { Flavor = MarkdownFlavor.GitHub, Text = "![](https://h/a%0Ab.png)\n\n[![](https://h/a%0D%0Ab.png)](https://outer.example)", Width = 400 };
            var window = new Window { Content = new ScrollViewer { Content = new StackPanel { Children = { view } } }, Width = 500, Height = 400 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            try {
                await Assert.That(view.Bounds.Height).IsGreaterThan(0);
                await Assert.That(Runs(view).Any(r => r.Text!.Contains('\n') || r.Text!.Contains('\r'))).IsFalse();
                await Assert.That(Images(view).Select(i => i.Label)).IsEquivalentTo(["a b.png", "a b.png"]);
            } finally { window.Close(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_alert_block_takes_the_app_brushes() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show("> [!NOTE]\n> Mind the gap.", MarkdownFlavor.GitHub);
            try {
                var alert = All<Border>(root).Single(b => b.Classes.Contains("markdown-alert"));
                await Assert.That(alert.Classes.Contains("markdown-alert-note")).IsTrue();
                await Assert.That(alert.BorderBrush).IsEqualTo((IBrush)Application.Current!.FindResource("KcapInfoBrush")!);
                await Assert.That(All<TextBlock>(alert).Select(Reads)).Contains("NOTE");
                await Assert.That(Paragraphs(alert).Select(Reads)).Contains("Mind the gap.");
            } finally { window.Close(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_chat_flavor_shows_comments_images_and_details_as_source() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show("<!-- c -->\n\n![alt](https://h/x.png)\n\n<details>\n<summary>S</summary>\n\nbody\n\n</details>");
            try {
                var texts = All<TextBlock>(root).Select(Reads).ToList();
                await Assert.That(texts).Contains("<!-- c -->");
                await Assert.That(texts).Contains("![alt](https://h/x.png)");
                await Assert.That(texts.Any(t => t.StartsWith("<details>", StringComparison.Ordinal))).IsTrue();
                await Assert.That(All<Button>(root)).IsEmpty();
            } finally { window.Close(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Changing_the_flavor_re_renders_the_current_text() {
        await RunOnUiAsync(async () => {
            var (window, view, _) = Show("p <b>x</b>");
            try {
                await Assert.That(Paragraphs(view).Select(Reads)).Contains("p <b>x</b>");
                view.Flavor = MarkdownFlavor.GitHub;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                await Assert.That(Spans<Bold>(Paragraphs(view).Single().Inlines!).Single().Inlines.Text).IsEqualTo("x");
                view.Flavor = MarkdownFlavor.Chat;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                await Assert.That(Paragraphs(view).Select(Reads)).Contains("p <b>x</b>");
            } finally { window.Close(); }
        });
    }

    /// An HTML table renders through MarkView's own table shape: a grid with header borders on
    /// the `thead` row and the cell text where the source put it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_html_table_renders_as_a_grid_with_a_header_row() {
        await RunOnUiAsync(async () => {
            const string table = "<table>\n<thead>\n<tr><th>Name</th><th>Status</th></tr>\n</thead>\n<tbody>\n<tr><td><code>build</code></td><td>✅ passing</td></tr>\n</tbody>\n</table>";
            var (window, root, _) = Show(table, MarkdownFlavor.GitHub);
            try {
                var grid = All<Grid>(root).Single(g => g.Classes.Contains("markdown-table"));
                await Assert.That(grid.ColumnDefinitions.Count).IsEqualTo(2);
                var cells = All<Border>(grid).Where(b => b.Classes.Contains("markdown-table-cell")).ToList();
                await Assert.That(cells.Count).IsEqualTo(4);
                await Assert.That(cells.Count(b => b.Classes.Contains("markdown-table-header"))).IsEqualTo(2);
                var texts = All<TextBlock>(grid).Select(Reads).ToList();
                await Assert.That(texts).Contains("Name");
                await Assert.That(texts).Contains("✅ passing");
                await Assert.That(Runs(grid).Any(r => r.Text == "build" && r.Classes.Contains("markdown-code-inline"))).IsTrue();
                await Assert.That(All<TextBlock>(root).Select(Reads).Any(text => text.Contains('<'))).IsFalse();
            } finally { window.Close(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Html_headings_rules_and_definition_lists_take_the_markdown_shapes() {
        await RunOnUiAsync(async () => {
            const string markdown = "<h3>PR Summary</h3>\n\n<dl>\n<dd>\n\nindented\n\n</dd>\n</dl>\n\n<hr/>\n\n<div>after</div>";
            var (window, root, _) = Show(markdown, MarkdownFlavor.GitHub);
            try {
                var heading = All<TextBlock>(root).Single(t => t.Classes.Contains("markdown-h3"));
                await Assert.That(Reads(heading)).IsEqualTo("PR Summary");
                var indent = All<Border>(root).Single(b => b.Classes.Contains("markdown-indent"));
                await Assert.That(indent.Padding.Left).IsGreaterThan(0);
                await Assert.That(Paragraphs(indent).Select(Reads)).Contains("indented");
                await Assert.That(All<Separator>(root).Count(s => s.Classes.Contains("markdown-thematic-break"))).IsEqualTo(1);
                await Assert.That(Paragraphs(root).Select(Reads)).Contains("after");
                await Assert.That(All<TextBlock>(root).Select(Reads).Any(text => text.Contains('<'))).IsFalse();
            } finally { window.Close(); }
        });
    }

    /// Pins the pre's shape against MarkView's list-item selection walker, which does not index
    /// the library's own code-block shape.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_pre_inside_a_list_item_is_selectable() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show("- item\n\n  <pre>\n  alpha\n  beta\n  </pre>", MarkdownFlavor.GitHub);
            try {
                var viewer = Viewer(root);
                viewer.SelectAll();
                var selected = viewer.GetSelectedText();
                await Assert.That(selected).Contains("alpha");
                await Assert.That(selected).Contains("beta");
            } finally { window.Close(); }
        });
    }

    /// The refused normalisation shapes: deep under block quotes an autolink stays an autolink and
    /// a childless image keeps its label; both still read, and a direct autolink still opens.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Timeout(60_000)]
    public async Task Refused_normalisation_still_reads_and_opens(CancellationToken _) {
        await RunOnUiAsync(async () => {
            var (window, root, opened) = Show(Quoted(97, "<https://example.com> ![](https://h/name.png)"), MarkdownFlavor.GitHub, width: 3800);
            try {
                var texts = All<TextBlock>(root).Select(Reads).ToList();
                await Assert.That(texts.Any(t => t.Contains("https://example.com"))).IsTrue();
                await Assert.That(texts.Any(t => t.Contains("name.png"))).IsTrue();
                var (block, link) = AllLinks(root).Single(l => l.Link.NavigateUri?.ToString() == "https://example.com/");
                Click(window, block, link);
                await Assert.That(opened).IsEquivalentTo(new[] { "https://example.com/" });
            } finally { window.Close(); }

            var (boldWindow, boldRoot, _) = Show(Quoted(96, "**![](https://h/name.png)**"), MarkdownFlavor.GitHub, width: 3800);
            try {
                await Assert.That(Images(boldRoot).Single().Label).IsEqualTo("name.png");
            } finally { boldWindow.Close(); }
        });
    }
}
