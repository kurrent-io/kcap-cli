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
    Stop? _current;

    /// One keyboard stop: a hyperlink span in its text block, or a linked image, which has no span.
    sealed record Stop(Control Host, MarkdownHyperlink? Span, string Url) {
        public bool IsSame(Stop other) => Span is not null ? ReferenceEquals(Span, other.Span) : ReferenceEquals(Host, other.Host);
    }

    MarkdownLinkKeys(MarkdownViewer viewer, Func<ICommand?> openLink) {
        _viewer = viewer;
        _openLink = openLink;
    }

    public static MarkdownLinkKeys Attach(MarkdownViewer viewer, Func<ICommand?> openLink) {
        var keys = new MarkdownLinkKeys(viewer, openLink);
        viewer.GotFocus += (_, e) => keys.OnGotFocus(e);
        viewer.LostFocus += (_, _) => keys.Clear();
        viewer.AddHandler(InputElement.KeyDownEvent, (_, e) => keys.OnKeyDown(e), RoutingStrategies.Tunnel);
        // A press is the pointer taking over: Enter must not then open a link chosen before it.
        viewer.AddHandler(InputElement.PointerPressedEvent, (_, _) => keys.Clear(), RoutingStrategies.Tunnel, handledEventsToo: true);
        return keys;
    }

    /// The address of the link the keyboard is on, for a test to read.
    internal string? CurrentUrl => _current?.Url;

    /// A re-render replaces every span, so the one held no longer exists.
    public void Clear() {
        if (_current is { } current) AdornerLayer.SetAdorner(current.Host, null);
        _current = null;
    }

    void OnGotFocus(FocusChangedEventArgs e) {
        Clear();
        // Routed: a focusable descendant, such as a code block's action, raises it here too.
        if (!ReferenceEquals(e.Source, _viewer) || e.NavigationMethod != NavigationMethod.Tab) return;

        var links = Links();
        if (links.Count == 0) return;
        Select(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? links[^1] : links[0]);
    }

    void OnKeyDown(KeyEventArgs e) {
        if (!ReferenceEquals(e.Source, _viewer)) return;

        if (_current is not { } current) {
            // The viewer kept focus while its links were replaced, or a press cleared the link:
            // Tab starts on the new links rather than leaving past them.
            if (e.Key == Key.Tab && (e.KeyModifiers & ~KeyModifiers.Shift) == KeyModifiers.None && Links() is { Count: > 0 } fresh) {
                Select(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? fresh[^1] : fresh[0]);
                e.Handled = true;
            }
            return;
        }

        switch (e.Key) {
            case Key.Tab when (e.KeyModifiers & ~KeyModifiers.Shift) == KeyModifiers.None:
                var links = Links();
                var at = links.FindIndex(current.IsSame);
                var next = at < 0 ? -1 : at + (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                if (next < 0 || next >= links.Count) {
                    Clear();
                    return; // unhandled: focus moves on to the next control
                }
                Select(links[next]);
                e.Handled = true;
                break;
            case Key.Enter or Key.Space when e.KeyModifiers == KeyModifiers.None:
                if (_openLink() is { } open && open.CanExecute(current.Url)) open.Execute(current.Url);
                e.Handled = true;
                break;
        }
    }

    void Select(Stop stop) {
        Clear();
        _current = stop;
        AdornerLayer.SetAdorner(stop.Host, new LinkFocusRing(stop.Host, () => Rects(stop)));
        if (Rects(stop) is [var first, ..]) stop.Host.BringIntoView(first.Inflate(4));
    }

    static IReadOnlyList<Rect> Rects(Stop stop) =>
        stop.Span is { } span && stop.Host is TextBlock block ? LinkRects(block, span) : [new Rect(stop.Host.Bounds.Size)];

    /// In tree order: the hyperlinks of each text block, and each image that links somewhere. A link with
    /// no text has nowhere to draw its mark, so the keyboard passes over it.
    List<Stop> Links() =>
        [.. _viewer.GetVisualDescendants().SelectMany(visual => visual switch {
            TextBlock { Inlines: { } inlines } block => Spans(inlines)
                .Where(link => link.NavigateUri is not null && HasText(link.Inlines))
                .Select(link => new Stop(block, link, link.NavigateUri!.ToString())),
            MarkdownImage { Target: { } target } image => [new Stop(image, null, target)],
            _ => [],
        })];

    /// A span holding only a linked image is that image's stop, not one of its own.
    static bool HasText(InlineCollection inlines) =>
        inlines.Any(inline => inline is Span span ? HasText(span.Inlines) : inline is Run { Text.Length: > 0 });

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
        return [.. block.TextLayout.HitTestTextRange(start, length)
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
