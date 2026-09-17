using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Views;
using MarkView.Avalonia;
using MarkView.Avalonia.Rendering.Inlines;
using ReactiveUI.Reactive;

namespace Capacitor.App.Tests.Unit;

/// Shows a `MarkdownView` in a headless window and reads what it rendered. Every caller runs
/// inside `AvaloniaSession.RunOnUiAsync` and carries `[NotInParallel("AvaloniaSession")]`.
internal static class MarkdownViewHarness {
    public static (Window Window, MarkdownView View, List<string> Opened) Show(string markdown, MarkdownFlavor flavor = MarkdownFlavor.Chat, double width = 400) {
        var opened = new List<string>();
        ICommand open = ReactiveCommand.Create<string>(opened.Add);
        var view = new MarkdownView { Flavor = flavor, Text = markdown, OpenLink = open, Width = width };
        var window = new Window { Content = view, Width = width + 100, Height = 400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return (window, view, opened);
    }

    public static IEnumerable<T> All<T>(Visual root) where T : Visual => root.GetVisualDescendants().OfType<T>();

    public static MarkdownViewer Viewer(Visual root) => All<MarkdownViewer>(root).Single();

    public static IEnumerable<TextBlock> Paragraphs(Visual root) => All<TextBlock>(root).Where(t => t.Classes.Contains("markdown-paragraph"));

    /// A text block built from inlines leaves Text null and carries its characters on the
    /// inline collection, so a "what does this block read as" assertion has to consult both.
    public static string Reads(TextBlock block) => block.Text ?? block.Inlines?.Text ?? "";

    public static IEnumerable<T> Spans<T>(InlineCollection inlines) where T : Inline {
        foreach (var inline in inlines) {
            if (inline is T t) yield return t;
            if (inline is Span span) foreach (var nested in Spans<T>(span.Inlines)) yield return nested;
        }
    }

    public static IEnumerable<MarkdownHyperlink> Links(Visual root) => Paragraphs(root).SelectMany(p => Spans<MarkdownHyperlink>(p.Inlines!));

    public static IEnumerable<(TextBlock Block, MarkdownHyperlink Link)> AllLinks(Visual root) =>
        All<TextBlock>(root).Where(t => t.Inlines is not null).SelectMany(t => Spans<MarkdownHyperlink>(t.Inlines!).Select(link => (t, link)));

    public static IEnumerable<Run> Runs(Visual root) => All<TextBlock>(root).Where(t => t.Inlines is not null).SelectMany(t => Spans<Run>(t.Inlines!));

    /// The index of the hyperlink's first character in its block, measured as MarkView measures
    /// when it resolves a press: a run its length, a line break the newline's length, else one.
    public static int StartOf(TextBlock block, MarkdownHyperlink link) {
        var found = -1;
        Walk(block.Inlines!, link, ref found, 0);
        return found;
    }

    static int Walk(InlineCollection inlines, MarkdownHyperlink target, ref int found, int offset) {
        foreach (var inline in inlines) {
            if (ReferenceEquals(inline, target)) { found = offset; return offset; }
            switch (inline) {
                case Run run: offset += run.Text?.Length ?? 0; break;
                case LineBreak: offset += Environment.NewLine.Length; break;
                case Span span:
                    offset = Walk(span.Inlines, target, ref found, offset);
                    if (found >= 0) return offset;
                    break;
                default: offset += 1; break;
            }
        }
        return offset;
    }

    /// A press just inside the leading edge of the glyph at `index`, the way MarkView's caret-based
    /// dispatch expects one.
    public static void ClickAt(Window window, TextBlock block, int index) {
        var glyph = block.TextLayout.HitTestTextPosition(index);
        var point = block.TranslatePoint(new Point(glyph.X + block.Padding.Left + 2, glyph.Y + block.Padding.Top + glyph.Height / 2), window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    public static void Click(Window window, TextBlock block, MarkdownHyperlink link) => ClickAt(window, block, StartOf(block, link));
}
