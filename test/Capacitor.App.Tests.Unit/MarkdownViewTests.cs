using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Views;
using MarkView.Avalonia.Rendering.Inlines;
using ReactiveUI.Reactive;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit;

public class MarkdownViewTests {
    static (Window Window, Control Root, List<string> Opened) Show(string markdown) {
        var opened = new List<string>();
        ICommand open = ReactiveCommand.Create<string>(opened.Add);
        var view = new MarkdownView { Text = markdown, OpenLink = open, Width = 400 };
        var window = new Window { Content = view, Width = 500, Height = 400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return (window, view, opened);
    }

    static IEnumerable<T> All<T>(Visual root) where T : Visual => root.GetVisualDescendants().OfType<T>();

    static IEnumerable<TextBlock> Paragraphs(Visual root) => All<TextBlock>(root).Where(t => t.Classes.Contains("markdown-paragraph"));

    /// A text block built from inlines leaves Text null and carries its characters on the
    /// inline collection, so a "what does this block read as" assertion has to consult both.
    static string Reads(TextBlock block) => block.Text ?? block.Inlines?.Text ?? "";

    static IEnumerable<T> Spans<T>(InlineCollection inlines) where T : Inline {
        foreach (var inline in inlines) {
            if (inline is T t) yield return t;
            if (inline is Span span) foreach (var nested in Spans<T>(span.Inlines)) yield return nested;
        }
    }

    static IEnumerable<MarkdownHyperlink> Links(Visual root) => Paragraphs(root).SelectMany(p => Spans<MarkdownHyperlink>(p.Inlines!));

    /// Pins the block map: emphasis and code spans as inlines, fenced code, bullets, a quote and
    /// a rule, each carrying the style class the app's theme keys on.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Paragraph_inlines_code_blocks_lists_and_quotes_render() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show("Some **bold** and *em* and `code`.\n\n```\nvar x = 1;\n```\n\n- one\n- two\n\n> quoted\n\n---\n");
            try {
                var paragraph = Paragraphs(root).First();
                await Assert.That(Spans<Bold>(paragraph.Inlines!).Count()).IsEqualTo(1);
                await Assert.That(Spans<Italic>(paragraph.Inlines!).Count()).IsEqualTo(1);
                await Assert.That(Spans<Run>(paragraph.Inlines!).Any(r => r.Text == "code" && r.Classes.Contains("markdown-code-inline"))).IsTrue();

                var code = All<Border>(root).Single(b => b.Classes.Contains("markdown-code-block"));
                await Assert.That(Reads(All<TextBlock>(code).Single())).IsEqualTo("var x = 1;");
                await Assert.That(All<TextBlock>(root).Count(t => t.Classes.Contains("markdown-list-marker") && t.Text == "•")).IsEqualTo(2);
                var quote = All<Border>(root).Single(b => b.Classes.Contains("markdown-blockquote"));
                await Assert.That(Paragraphs(quote).Select(Reads)).IsEquivalentTo(["quoted"]);
                await Assert.That(All<Separator>(root).Count(s => s.Classes.Contains("markdown-thematic-break"))).IsEqualTo(1);
            } finally { window.Close(); }
        });
    }

    /// Pins the link affordance: a policy-approved link is an inline hyperlink that opens through
    /// the command on a pointer click, never through a self-navigating control.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_allowed_link_opens_through_the_command_on_click() {
        await RunOnUiAsync(async () => {
            var (window, root, opened) = Show("See [docs](https://example.com/docs) now.");
            try {
                var link = Links(root).Single();
                await Assert.That(link.NavigateUri?.ToString()).IsEqualTo("https://example.com/docs");
                await Assert.That(All<Button>(root)).IsEmpty();

                var paragraph = Paragraphs(root).Single();
                var glyph = paragraph.TextLayout.HitTestTextPosition("See ".Length);
                var point = paragraph.TranslatePoint(new Point(glyph.X + paragraph.Padding.Left + 2, glyph.Y + paragraph.Padding.Top + glyph.Height / 2), window)!.Value;
                window.MouseMove(point);
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                await Assert.That(opened).IsEquivalentTo(new[] { "https://example.com/docs" });
            } finally { window.Close(); }
        });
    }

    /// Pins the trust boundary and the degrade rule: a refused scheme leaves no link behind,
    /// whether written as a link or as a bare URL, and HTML keeps its source text.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_disallowed_link_and_unknown_constructs_render_as_plain_text() {
        await RunOnUiAsync(async () => {
            var (window, root, opened) = Show("Bad [link](javascript:alert(1)) and <b>html</b> and ftp://x.example/f\n\n<div>raw</div>\n");
            try {
                await Assert.That(Links(root)).IsEmpty();
                await Assert.That(All<Button>(root)).IsEmpty();
                var paragraphs = Paragraphs(root).Select(Reads).ToList();
                await Assert.That(paragraphs).Contains("Bad link and <b>html</b> and ftp://x.example/f");
                await Assert.That(paragraphs).Contains("<div>raw</div>");
                await Assert.That(opened).IsEmpty();
            } finally { window.Close(); }
        });
    }

    /// Pins the autolink path: a bare https URL is the same inline hyperlink as a written link,
    /// not a button that would navigate on its own.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_bare_https_url_is_an_inline_link_without_a_button() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show("Go to https://example.com/x now.");
            try {
                var link = Links(root).Single();
                await Assert.That(link.NavigateUri?.ToString()).IsEqualTo("https://example.com/x");
                await Assert.That(link.Inlines.Text).IsEqualTo("https://example.com/x");
                await Assert.That(All<Button>(root)).IsEmpty();
            } finally { window.Close(); }
        });
    }

    /// Pins what a newline may become: a hard break is a line break inside the paragraph, a soft
    /// break is a space, and an entity reaches the block decoded.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Hard_breaks_are_line_breaks_soft_breaks_are_spaces_and_entities_decode() {
        await RunOnUiAsync(async () => {
            var (hard, hardRoot, _) = Show("a  \nb");
            try {
                var paragraph = Paragraphs(hardRoot).Single();
                await Assert.That(paragraph.Inlines!.OfType<LineBreak>().Count()).IsEqualTo(1);
                await Assert.That(paragraph.Inlines!.OfType<Run>().Select(r => r.Text!)).IsEquivalentTo(["a", "b"]);
            } finally { hard.Close(); }

            var (soft, softRoot, _) = Show("a\nb");
            try {
                await Assert.That(Paragraphs(softRoot).Select(Reads)).IsEquivalentTo(["a b"]);
            } finally { soft.Close(); }

            var (entity, entityRoot, _) = Show("a &amp; b");
            try {
                await Assert.That(Paragraphs(entityRoot).Select(Reads)).IsEquivalentTo(["a & b"]);
            } finally { entity.Close(); }
        });
    }

    /// Pins the image and label rules: an image keeps its source text and fetches nothing, and a
    /// label's code span survives inside the link.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Images_keep_their_source_text_and_link_labels_keep_their_code_spans() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show("![alt](https://example.com/y.png) and [see `code` here](https://example.com/d)");
            try {
                var paragraph = Paragraphs(root).Single();
                await Assert.That(paragraph.Inlines!.OfType<Run>().Any(r => r.Text == "![alt](https://example.com/y.png)")).IsTrue();
                await Assert.That(All<Image>(root)).IsEmpty();
                await Assert.That(paragraph.Inlines!.OfType<InlineUIContainer>()).IsEmpty();

                var link = Links(root).Single();
                await Assert.That(link.Inlines.Text).IsEqualTo("see code here");
                await Assert.That(Spans<Run>(link.Inlines).Any(r => r.Text == "code" && r.Classes.Contains("markdown-code-inline"))).IsTrue();
            } finally { window.Close(); }
        });
    }

    /// Pins table rendering: a pipe table is a grid with one row per source row, header cells
    /// marked as such, inline-rendered cells, and the column alignment the separator row declares.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_pipe_table_renders_as_a_grid_with_a_header_and_aligned_cells() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show("| Issue | Count |\n|---|--:|\n| `x` first | 2 |\n| second | 10 |");
            try {
                var table = All<Grid>(root).Single(g => g.Classes.Contains("markdown-table"));
                await Assert.That(table.RowDefinitions.Count).IsEqualTo(3);
                await Assert.That(table.ColumnDefinitions.Count).IsEqualTo(2);

                var cells = table.Children.OfType<Border>().Where(b => b.Classes.Contains("markdown-table-cell")).ToList();
                await Assert.That(cells.Count).IsEqualTo(6);
                await Assert.That(cells.Take(2).All(c => c.Classes.Contains("markdown-table-header"))).IsTrue();
                await Assert.That(cells.Skip(2).Any(c => c.Classes.Contains("markdown-table-header"))).IsFalse();
                await Assert.That(cells.Select(c => Reads(Paragraphs(c).Single()))).IsEquivalentTo(["Issue", "Count", "x first", "2", "second", "10"]);
                await Assert.That(Spans<Run>(Paragraphs(cells[2]).Single().Inlines!).Any(r => r.Classes.Contains("markdown-code-inline"))).IsTrue();
                await Assert.That(((Control)cells[3].Child!).HorizontalAlignment).IsEqualTo(HorizontalAlignment.Right);
                await Assert.That(((Control)cells[2].Child!).HorizontalAlignment).IsNotEqualTo(HorizontalAlignment.Right);
            } finally { window.Close(); }
        });
    }

    /// Pins diff highlighting: a fenced diff block colours its removed and added lines, and
    /// differently from each other.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_diff_block_colours_added_and_removed_lines() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show("```diff\n- removed line\n+ added line\n  context\n```");
            try {
                var block = All<Border>(root).Single(b => b.Classes.Contains("markdown-code-block"));
                await Assert.That(block.Classes.Contains("language-diff")).IsTrue();
                var runs = All<TextBlock>(block).Single().Inlines!.OfType<Run>().ToList();
                var removed = runs.First(r => r.Text!.Contains("removed"));
                var added = runs.First(r => r.Text!.Contains("added"));
                await Assert.That(removed.Foreground).IsNotNull();
                await Assert.That(added.Foreground).IsNotNull();
                await Assert.That(((ISolidColorBrush)removed.Foreground!).Color).IsNotEqualTo(((ISolidColorBrush)added.Foreground!).Color);
            } finally { window.Close(); }
        });
    }

    /// Pins the theme hook: the app's brushes reach the rendered text through the style classes,
    /// so a MarkView theme default never paints over the Kcap palette.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Rendered_text_takes_the_app_brushes() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show("Some `code` here.\n\n```\nx\n```");
            try {
                var text = (IBrush)Application.Current!.FindResource("KcapTextBrush")!;
                var paragraph = Paragraphs(root).Single();
                await Assert.That(paragraph.Foreground).IsEqualTo(text);
                var code = Spans<Run>(paragraph.Inlines!).Single(r => r.Classes.Contains("markdown-code-inline"));
                await Assert.That(code.Foreground).IsEqualTo(text);
                var block = All<Border>(root).Single(b => b.Classes.Contains("markdown-code-block"));
                await Assert.That(block.Background).IsEqualTo((IBrush)Application.Current!.FindResource("KcapSurfaceBrush")!);
            } finally { window.Close(); }
        });
    }
}
