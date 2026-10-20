using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MarkView.Avalonia;
using MarkView.Avalonia.Rendering.Inlines;

namespace Capacitor.App.Views;

/// Keyboard access to the links MarkView renders as inline spans, which take no focus of their own.
/// Tab into the viewer lands on its first link (Shift+Tab on its last), Tab and Shift+Tab step through
/// them and then leave, and Enter or Space opens the current one through the same command a click
/// uses, so the link policy that decided which spans are links still decides what opens.
sealed class MarkdownLinkKeys {
    readonly MarkdownViewer _viewer;
    readonly Func<ICommand?> _openLink;
    (TextBlock Block, MarkdownHyperlink Link)? _current;

    MarkdownLinkKeys(MarkdownViewer viewer, Func<ICommand?> openLink) {
        _viewer = viewer;
        _openLink = openLink;
    }

    public static MarkdownLinkKeys Attach(MarkdownViewer viewer, Func<ICommand?> openLink) {
        var keys = new MarkdownLinkKeys(viewer, openLink);
        viewer.GotFocus += (_, e) => keys.OnGotFocus(e);
        viewer.LostFocus += (_, _) => keys.Clear();
        viewer.AddHandler(InputElement.KeyDownEvent, (_, e) => keys.OnKeyDown(e), RoutingStrategies.Tunnel);
        return keys;
    }

    /// The link the keyboard is on, for a test to read.
    internal MarkdownHyperlink? Current => _current?.Link;

    /// A re-render replaces every span, so the one held no longer exists.
    public void Clear() {
        if (_current is { } current) AdornerLayer.SetAdorner(current.Block, null);
        _current = null;
    }

    void OnGotFocus(FocusChangedEventArgs e) {
        Clear();
        if (e.NavigationMethod != NavigationMethod.Tab) return;

        var links = Links();
        if (links.Count == 0) return;
        Select(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? links[^1] : links[0]);
    }

    void OnKeyDown(KeyEventArgs e) {
        if (_current is not { } current) return;

        switch (e.Key) {
            case Key.Tab when (e.KeyModifiers & ~KeyModifiers.Shift) == KeyModifiers.None:
                var links = Links();
                var at = links.FindIndex(l => ReferenceEquals(l.Link, current.Link));
                var next = at < 0 ? -1 : at + (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                if (next < 0 || next >= links.Count) {
                    Clear();
                    return; // unhandled: focus moves on to the next control
                }
                Select(links[next]);
                e.Handled = true;
                break;
            case Key.Enter or Key.Space when e.KeyModifiers == KeyModifiers.None:
                var url = current.Link.NavigateUri?.ToString();
                if (url is not null && _openLink() is { } open && open.CanExecute(url)) open.Execute(url);
                e.Handled = true;
                break;
        }
    }

    void Select((TextBlock Block, MarkdownHyperlink Link) link) {
        Clear();
        _current = link;
        var rects = LinkRects(link.Block, link.Link);
        AdornerLayer.SetAdorner(link.Block, new LinkFocusRing(rects));
        if (rects.Count > 0) link.Block.BringIntoView(rects[0].Inflate(4));
    }

    List<(TextBlock Block, MarkdownHyperlink Link)> Links() =>
        [.. _viewer.GetVisualDescendants().OfType<TextBlock>()
            .Where(b => b.Inlines is not null)
            .SelectMany(b => Spans(b.Inlines!).Select(link => (b, link)))];

    static IEnumerable<MarkdownHyperlink> Spans(InlineCollection inlines) {
        foreach (var inline in inlines) {
            if (inline is MarkdownHyperlink link) yield return link;
            else if (inline is Span span) foreach (var nested in Spans(span.Inlines)) yield return nested;
        }
    }

    /// The link's text bounds in its block, measured the way MarkView measures a press: a run counts
    /// its length, a line break the newline's, anything else one.
    static IReadOnlyList<Rect> LinkRects(TextBlock block, MarkdownHyperlink link) {
        var start = -1;
        Offset(block.Inlines!, link, ref start, 0);
        if (start < 0) return [];

        var length = Length(link.Inlines);
        var padding = block.Padding;
        return [.. block.TextLayout.HitTestTextRange(start, Math.Max(length, 1))
            .Select(r => r.Translate(new Vector(padding.Left, padding.Top)))];
    }

    static int Offset(InlineCollection inlines, Inline target, ref int found, int offset) {
        foreach (var inline in inlines) {
            if (ReferenceEquals(inline, target)) { found = offset; return offset; }
            if (inline is Span span) {
                offset = Offset(span.Inlines, target, ref found, offset);
                if (found >= 0) return offset;
            } else offset += Width(inline);
        }
        return offset;
    }

    static int Length(InlineCollection inlines) =>
        inlines.Sum(inline => inline is Span span ? Length(span.Inlines) : Width(inline));

    static int Width(Inline inline) => inline switch {
        Run run => run.Text?.Length ?? 0,
        LineBreak => Environment.NewLine.Length,
        _ => 1,
    };
}
