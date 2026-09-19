using System.Reactive.Subjects;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Capacitor.App.Views;
using MarkView.Avalonia;
using ReactiveUI.Reactive;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.MarkdownViewHarness;

namespace Capacitor.App.Tests.Unit;

public class MarkdownViewTests {
    static (Window Window, Control Root, List<string> Opened, List<string> Ran) ShowRunnable(string markdown) {
        var opened = new List<string>();
        var ran = new List<string>();
        var view = new MarkdownView {
            Text = markdown,
            OpenLink = ReactiveCommand.Create<string>(opened.Add),
            RunCode = ReactiveCommand.Create<string>(ran.Add),
            Width = 400,
        };
        var window = new Window { Content = view, Width = 500, Height = 400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return (window, view, opened, ran);
    }

    static List<Panel> Hosts(Visual root) => All<Panel>(root).Where(p => p.Classes.Contains("markdown-code-host")).ToList();

    static Control Strip(Visual host) => All<Control>(host).First(c => c.Classes.Contains("markdown-code-actions"));

    static List<Button> Actions(Visual host, string kind) =>
        All<Button>(host).Where(b => b.Classes.Contains(kind)).ToList();

    static Point Centre(Visual target, Window window) =>
        target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;

    static void Hover(Window window, Visual target) {
        window.MouseMove(Centre(target, window));
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    /// A strip button has no bounds to aim at until the hover that reveals it has been laid out.
    static Button Reveal(Window window, Visual host, string kind) {
        Hover(window, host);
        return Actions(host, kind).Single();
    }

    static void Click(Window window, Visual target) {
        var point = Centre(target, window);
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

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
                await Assert.That(ReferenceEquals(link.Foreground, window.FindResource("KcapInfoBrush"))).IsTrue()
                    .Because("links are info blue; green is a status colour");

                var paragraph = Paragraphs(root).Single();
                ClickAt(window, paragraph, "See ".Length);
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

    /// Pins the layout guard: a multi-line HTML block and a hard break lay out under a parent that
    /// leaves height unconstrained, the shape of every chat list and reader pane, and reach the
    /// block as line breaks between runs, never as a newline inside one.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Timeout(30_000)]
    public async Task Multi_line_html_and_hard_breaks_lay_out_under_an_unconstrained_parent(CancellationToken _) {
        await RunOnUiAsync(async () => {
            var view = new MarkdownView { Text = "<div>\n  <p>text</p>\n</div>\n\nline one  \nline two", Width = 400 };
            var window = new Window { Content = new ScrollViewer { Content = new StackPanel { Children = { view } } }, Width = 500, Height = 400 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            try {
                await Assert.That(view.Bounds.Height).IsGreaterThan(0);
                var paragraphs = Paragraphs(view).ToList();
                await Assert.That(paragraphs.Count).IsEqualTo(2);
                await Assert.That(paragraphs[0].Inlines!.OfType<LineBreak>().Count()).IsEqualTo(2);
                await Assert.That(paragraphs[1].Inlines!.OfType<LineBreak>().Count()).IsEqualTo(1);
                var runs = paragraphs.SelectMany(p => Spans<Run>(p.Inlines!)).Select(r => r.Text!).ToList();
                await Assert.That(runs.Any(t => t.Contains('\n'))).IsFalse();
                await Assert.That(runs.First()).IsEqualTo("<div>");
                await Assert.That(runs.Any(t => t.Contains("<p>text</p>"))).IsTrue();
            } finally { window.Close(); }
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

    /// Pins the reveal rule: the strip is out of reach until the pointer is over its own block, so
    /// resting code carries no chrome and a hover never arms the neighbouring block's buttons.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_action_strip_appears_only_over_the_block_the_pointer_is_on() {
        await RunOnUiAsync(async () => {
            var (window, root, _, _) = ShowRunnable("```\nfirst\n```\n\n```\nsecond\n```");
            try {
                var hosts = Hosts(root);
                await Assert.That(hosts.Count).IsEqualTo(2);
                await Assert.That(Strip(hosts[0]).Opacity).IsEqualTo(0);
                await Assert.That(Strip(hosts[0]).IsHitTestVisible).IsFalse();
                await Assert.That(Strip(hosts[1]).Opacity).IsEqualTo(0);

                Hover(window, hosts[0]);
                await Assert.That(Strip(hosts[0]).Opacity).IsEqualTo(1);
                await Assert.That(Strip(hosts[0]).IsHitTestVisible).IsTrue();
                await Assert.That(Strip(hosts[1]).Opacity).IsEqualTo(0);
            } finally { window.Close(); }
        });
    }

    /// Pins keyboard reach: hiding the strip by making it invisible would drop its buttons out of
    /// tab navigation, leaving the whole affordance available to a pointer alone.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_action_strip_is_reachable_and_revealed_by_keyboard_focus() {
        await RunOnUiAsync(async () => {
            var (window, root, _, ran) = ShowRunnable("```bash\n! kcap agent ls\n```");
            try {
                var host = Hosts(root)[0];
                var run = Actions(host, "markdown-code-run").Single();
                await Assert.That(run.Focusable).IsTrue();

                run.Focus();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                await Assert.That(Strip(host).Opacity).IsEqualTo(1);

                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                Dispatcher.UIThread.RunJobs();
                await Assert.That(ran).IsEquivalentTo(new[] { "! kcap agent ls" });
            } finally { window.Close(); }
        });
    }

    /// Pins the bang's position: the text is sent verbatim, so a bang the block only reaches past
    /// some whitespace would send something that is not a command. Copy is offered regardless.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_block_whose_bang_follows_whitespace_offers_copy_alone() {
        await RunOnUiAsync(async () => {
            var (window, root, _, _) = ShowRunnable("```bash\n   ! kcap agent ls\n```");
            try {
                await Assert.That(Actions(Hosts(root)[0], "markdown-code-run")).IsEmpty();
                await Assert.That(Actions(Hosts(root)[0], "markdown-code-copy").Count).IsEqualTo(1);
            } finally { window.Close(); }
        });
    }

    /// Pins the button against the command's own gate: a composer that cannot take the command
    /// greys the button rather than leaving a live-looking control whose click does nothing.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_run_button_is_disabled_while_the_command_refuses() {
        await RunOnUiAsync(async () => {
            var refuse = new BehaviorSubject<bool>(false);
            var view = new MarkdownView {
                Text = "```bash\n! kcap agent ls\n```",
                RunCode = ReactiveCommand.Create<string>(_ => { }, refuse),
                Width = 400,
            };
            var window = new Window { Content = view, Width = 500, Height = 400 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            try {
                var run = Actions(Hosts(view)[0], "markdown-code-run").Single();
                await Assert.That(run.IsEffectivelyEnabled).IsFalse();

                refuse.OnNext(true);
                Dispatcher.UIThread.RunJobs();
                await Assert.That(run.IsEffectivelyEnabled).IsTrue();
            } finally { window.Close(); }
        });
    }

    /// Pins the copied text against the block on screen: the line group ends where the block's last
    /// line does, so a blank line the block shows is a blank line the clipboard carries.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_blank_last_line_survives_the_copy() {
        await RunOnUiAsync(async () => {
            var (window, root, _, _) = ShowRunnable("```bash\nls -la\n\n```");
            try {
                Click(window, Reveal(window, Hosts(root)[0], "markdown-code-copy"));
                var clipboard = TopLevel.GetTopLevel(root)!.Clipboard!;
                await Assert.That(await clipboard.TryGetTextAsync()).IsEqualTo("ls -la\n");
            } finally { window.Close(); }
        });
    }

    /// Pins the run affordance's one condition: the block's own text begins with the bang that
    /// makes it a command to run. Copy is offered either way.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Only_a_bang_prefixed_block_offers_to_run_it() {
        await RunOnUiAsync(async () => {
            var (window, root, _, _) = ShowRunnable("```bash\n! kcap agent ls\n```\n\n```bash\ngit status\n```");
            try {
                var hosts = Hosts(root);
                await Assert.That(Actions(hosts[0], "markdown-code-run").Count).IsEqualTo(1);
                await Assert.That(Actions(hosts[1], "markdown-code-run")).IsEmpty();
                await Assert.That(Actions(hosts[0], "markdown-code-copy").Count).IsEqualTo(1);
                await Assert.That(Actions(hosts[1], "markdown-code-copy").Count).IsEqualTo(1);
            } finally { window.Close(); }
        });
    }

    /// Pins what "run it" hands over: the block's text verbatim, bang and all, since that bang is
    /// what the composer needs to treat the line as a command rather than a prompt.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Running_a_block_hands_its_text_to_the_command_bang_and_all() {
        await RunOnUiAsync(async () => {
            var (window, root, _, ran) = ShowRunnable("```bash\n! kcap agent ls\n```");
            try {
                Click(window, Reveal(window, Hosts(root)[0], "markdown-code-run"));
                await Assert.That(ran).IsEquivalentTo(new[] { "! kcap agent ls" });
            } finally { window.Close(); }
        });
    }

    /// Pins the copy path: every line of the block reaches the clipboard, with the fence's own
    /// trailing newline left off.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Copying_a_block_puts_its_every_line_on_the_clipboard() {
        await RunOnUiAsync(async () => {
            var (window, root, _, _) = ShowRunnable("```bash\ncd /tmp\nls -la\n```");
            try {
                Click(window, Reveal(window, Hosts(root)[0], "markdown-code-copy"));
                var clipboard = TopLevel.GetTopLevel(root)!.Clipboard!;
                await Assert.That(await clipboard.TryGetTextAsync()).IsEqualTo("cd /tmp\nls -la");
            } finally { window.Close(); }
        });
    }

    /// Pins the arrival order a binding actually lands in: the command reaches the view after the
    /// markdown has already rendered, and the block still ends up offering to run it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_run_command_set_after_the_text_still_reaches_the_block() {
        await RunOnUiAsync(async () => {
            var ran = new List<string>();
            var view = new MarkdownView { Text = "```bash\n! kcap agent ls\n```", Width = 400 };
            var window = new Window { Content = view, Width = 500, Height = 400 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            try {
                await Assert.That(Actions(Hosts(view)[0], "markdown-code-run")).IsEmpty();

                view.RunCode = ReactiveCommand.Create<string>(ran.Add);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Click(window, Reveal(window, Hosts(view)[0], "markdown-code-run"));
                await Assert.That(ran).IsEquivalentTo(new[] { "! kcap agent ls" });
            } finally { window.Close(); }
        });
    }

    /// Pins the surface rule: a view with no run command — the pull request reader — offers copy
    /// and nothing else, whatever the block's text begins with.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_view_without_a_run_command_offers_copy_alone() {
        await RunOnUiAsync(async () => {
            var (window, root, _) = Show("```bash\n! kcap agent ls\n```");
            try {
                await Assert.That(Actions(Hosts(root)[0], "markdown-code-copy").Count).IsEqualTo(1);
                await Assert.That(Actions(Hosts(root)[0], "markdown-code-run")).IsEmpty();
            } finally { window.Close(); }
        });
    }

    /// Pins the cost of the binding order every chat row lands in: the command arriving after the
    /// text adds the run offer to the strip already on screen, not a second build of the document.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_run_command_set_after_the_text_does_not_render_the_document_again() {
        await RunOnUiAsync(async () => {
            var view = new MarkdownView { Text = "```bash\n! kcap agent ls\n```\n\nSome prose.", Width = 400 };
            var window = new Window { Content = view, Width = 500, Height = 400 };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            try {
                var viewer = Viewer(view);
                var renders = 0;
                using var _ = MarkdownViewer.MarkdownProperty.Changed.Subscribe(e => {
                    if (ReferenceEquals(e.Sender, viewer) && e.NewValue.GetValueOrDefault() is not null) renders++;
                });
                var prose = Paragraphs(view).Single();

                view.RunCode = ReactiveCommand.Create<string>(_ => { });
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                await Assert.That(renders).IsEqualTo(0);
                await Assert.That(Paragraphs(view).Single()).IsSameReferenceAs(prose);
                await Assert.That(Actions(Hosts(view)[0], "markdown-code-run").Count).IsEqualTo(1);
            } finally { window.Close(); }
        });
    }

    /// Pins the reverse move: a command withdrawn from the view takes the run offer with it, and
    /// the document on screen is still the one that was built.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_run_command_withdrawn_takes_the_offer_without_rendering_again() {
        await RunOnUiAsync(async () => {
            var (window, root, _, _) = ShowRunnable("```bash\n! kcap agent ls\n```\n\nSome prose.");
            try {
                var viewer = Viewer(root);
                var renders = 0;
                using var _ = MarkdownViewer.MarkdownProperty.Changed.Subscribe(e => {
                    if (ReferenceEquals(e.Sender, viewer) && e.NewValue.GetValueOrDefault() is not null) renders++;
                });
                var prose = Paragraphs(root).Single();

                ((MarkdownView)root).RunCode = null;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                await Assert.That(renders).IsEqualTo(0);
                await Assert.That(Paragraphs(root).Single()).IsSameReferenceAs(prose);
                await Assert.That(Actions(Hosts(root)[0], "markdown-code-run")).IsEmpty();
                await Assert.That(Actions(Hosts(root)[0], "markdown-code-copy").Count).IsEqualTo(1);
            } finally { window.Close(); }
        });
    }
}
