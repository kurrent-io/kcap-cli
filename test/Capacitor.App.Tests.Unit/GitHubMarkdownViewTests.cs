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

    /// Pins the headline: the bot comment renders with no literal tag anywhere, the badge as a
    /// link labelled by its alt text, the emphasis inside the pre, and the code span.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_bot_comment_renders_with_no_literal_tags() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show(BotComment, MarkdownFlavor.GitHub);
            try {
                await Assert.That(All<TextBlock>(root).Select(Reads).Any(text => text.Contains('<'))).IsFalse();
                var badge = AllLinks(root).Single();
                await Assert.That(badge.Link.NavigateUri?.ToString()).IsEqualTo("https://img.shields.io/badge/Medium-634FD1");
                await Assert.That(badge.Link.Inlines.Text).IsEqualTo("Remediation recommended");
                await Assert.That(Runs(root).Any(r => r.Text == "📘 Rule violation" && r.Classes.Contains("markdown-code-inline"))).IsTrue();

                var pre = All<Border>(root).Single(b => b.Classes.Contains("markdown-pre"));
                await Assert.That(pre.Classes.Contains("markdown-code-block")).IsTrue();
                var text = All<MarkdownSelectableTextBlock>(pre).Single();
                var bold = Spans<Bold>(text.Inlines!).Single();
                await Assert.That(Spans<Italic>(bold.Inlines).Single().Inlines.Text).IsEqualTo("Name the repo's projects");
                await Assert.That(text.Inlines!.OfType<LineBreak>().Count()).IsEqualTo(1);
                await Assert.That(All<Image>(root)).IsEmpty();
                await Assert.That(All<TextBlock>(root).Any(t => t.Inlines?.OfType<InlineUIContainer>().Any() == true)).IsFalse();
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

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_image_is_a_link_that_opens_through_the_command() {
        await RunOnUiAsync(async () => {
            var (window, root, opened) = Show("See ![the shot](https://h/x.png) here.", MarkdownFlavor.GitHub);
            try {
                var (block, link) = AllLinks(root).Single();
                await Assert.That(link.Inlines.Text).IsEqualTo("the shot");
                Click(window, block, link);
                await Assert.That(opened).IsEquivalentTo(new[] { "https://h/x.png" });
            } finally { window.Close(); }
        });
    }

    /// Pins one hyperlink at a time, on ancestry and on the URL dispatched: the outermost openable
    /// link wins, and a refused outer link lets the inner one through.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments("[![badge](https://h/b.png)](https://outer.example)", "https://outer.example/", "badge")]
    [Arguments("<a href=\"https://outer.example\"><img src=\"https://h/b.png\" alt=\"badge\"></a>", "https://outer.example/", "badge")]
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

    /// Every link here sits under formatting, inside a pre, after an emoji, in a list item, or is
    /// an image — and each is a direct inline of a selectable text block that opens exactly once.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments("**[x](https://u.example/1)**", "https://u.example/1")]
    [Arguments("p <b><i><a href=\"https://u.example/2\">y</a></i></b>", "https://u.example/2")]
    [Arguments("**<https://example.com>**", "https://example.com/")]
    [Arguments("**![badge](https://h/b.png)**", "https://h/b.png")]
    [Arguments("**![](https://h/name.png)**", "https://h/name.png")]
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
        if (Environment.NewLine.Length != 1) return;
        await RunOnUiAsync(async () => {
            var (window, root, opened) = Show("<pre>first\n<a href=\"https://u.example/second\">second</a></pre>", MarkdownFlavor.GitHub);
            try {
                var (block, link) = AllLinks(root).Single();
                Click(window, block, link);
                await Assert.That(opened).IsEquivalentTo(new[] { "https://u.example/second" });
            } finally { window.Close(); }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_emphasised_image_reads_bold_with_either_label() {
        await RunOnUiAsync(async () => {
            foreach (var (markdown, label) in new[] { ("**![badge](https://h/b.png)**", "badge"), ("**![](https://h/name.png)**", "name.png") }) {
                var (window, root, _) = Show(markdown, MarkdownFlavor.GitHub);
                try {
                    var (_, link) = AllLinks(root).Single();
                    await Assert.That(Spans<Bold>(link.Inlines).Single().Inlines.Text).IsEqualTo(label);
                } finally { window.Close(); }
            }
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
                var view = new MarkdownView { Flavor = flavor, Text = "a <span\ntitle=\"x\">b</span> c\n\nd <b\nclass=\"x\">z", Width = 400 };
                var window = new Window { Content = new ScrollViewer { Content = new StackPanel { Children = { view } } }, Width = 500, Height = 400 };
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                try {
                    await Assert.That(view.Bounds.Height).IsGreaterThan(0);
                    await Assert.That(Runs(view).Any(r => r.Text!.Contains('\n') || r.Text!.Contains('\r'))).IsFalse();
                    var texts = Paragraphs(view).Select(p => Reads(p).ReplaceLineEndings("\n")).ToList();
                    await Assert.That(texts).Contains("a <span\ntitle=\"x\">b</span> c");
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
                await Assert.That(AllLinks(view).Select(l => l.Link.Inlines.Text!)).IsEquivalentTo(["a b.png", "a b.png"]);
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
    /// a childless image keeps its label, and each still reads and, where direct, still opens.
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
                var bold = Paragraphs(boldRoot).SelectMany(p => Spans<Bold>(p.Inlines!)).Single();
                await Assert.That(bold.Inlines.Text).IsEqualTo("name.png");
            } finally { boldWindow.Close(); }
        });
    }
}
