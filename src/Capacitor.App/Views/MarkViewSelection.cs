using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using MarkView.Avalonia;
using MarkView.Avalonia.Rendering;

namespace Capacitor.App.Views;

/// Selection behaviour MarkView lacks, built on its private selection layer: a double click
/// selects the line under the pointer, a drag that leaves every block keeps extending to the
/// nearest one, and a copied list item keeps its tail. Every member named below is MarkView's
/// private surface; `MarkViewSelectionTests` fails on a release that renames one.
static class MarkViewSelection {
    const string LayerType = "MarkView.Avalonia.Rendering.DocumentSelectionLayer, MarkView.Avalonia";
    const string EntryType = "MarkView.Avalonia.Rendering.IndexEntry, MarkView.Avalonia";

    // An accessor cannot name a field whose type is inaccessible, so these two are read by reflection.
    static readonly FieldInfo LayerField = Private(typeof(MarkdownViewer), "_selectionLayer");
    static readonly FieldInfo EntriesField = Private(LayerField.FieldType, "_entries");

    /// The copy shortcut runs the host's own copy, which knows whether the viewer is what it shows.
    public static void Attach(Control host, MarkdownViewer viewer, Func<Task> copy) {
        host.AddHandler(InputElement.PointerPressedEvent, (_, e) => OnPressed(viewer, e), RoutingStrategies.Bubble, handledEventsToo: true);
        host.AddHandler(InputElement.PointerMovedEvent, (_, e) => OnMoved(viewer, e), RoutingStrategies.Bubble, handledEventsToo: true);
        host.AddHandler(InputElement.KeyDownEvent, (_, e) => OnKeyDown(copy, e), RoutingStrategies.Tunnel);
    }

    /// Continues a drag at a point in the viewer's coordinates. Used when the chat scrolls under a
    /// pointer that is sitting on the viewport edge, so the selection grows without a new move.
    public static void Extend(MarkdownViewer viewer, Point pointInViewer) {
        if (LayerOf(viewer) is not Visual layer) return;
        if (Anchor(layer) is null) return;
        IsDragging(viewer) = true;
        var point = viewer.TranslatePoint(pointInViewer, layer) ?? pointInViewer;
        if (HitTestOffset(layer, point) is not null) MoveFocus(layer, point);
        else if (Nearest(layer, point) is { } inside) MoveFocus(layer, inside);
    }

    public static bool IsSelecting(MarkdownViewer viewer) =>
        IsDragging(viewer) && LayerOf(viewer) is { } layer && Anchor(layer) is not null;

    public static bool HasSelection(MarkdownViewer viewer) =>
        LayerOf(viewer) is { } layer && Anchor(layer) is { } anchor && Focus(layer) is { } focus && anchor != focus;

    /// MarkView's own extraction, except that a list item's marker counts as text that precedes
    /// the item rather than as its first characters. MarkView prefixes the item's first block
    /// with its marker but hit-tests that block's layout, which does not hold one, so every
    /// offset lands that many characters early and the item's tail is never copied.
    public static string SelectedText(MarkdownViewer viewer) {
        if (LayerOf(viewer) is not { } layer || Anchor(layer) is not { } anchor || Focus(layer) is not { } focus) return "";
        var (from, to) = (Math.Min(anchor, focus), Math.Max(anchor, focus));
        if (from == to) return "";
        var text = new StringBuilder();
        foreach (var entry in Entries(layer)) {
            var start = AbsStart(entry);
            if (start >= to) break;
            var plain = PlainText(entry);
            var marker = MarkerLength(entry, plain);
            var laidOut = plain.Length - marker;
            if (start + laidOut + Separator(entry).Length <= from) continue;
            var (s, e) = (Math.Max(0, from - start), Math.Min(laidOut, to - start));
            if (s == 0 && marker > 0) text.Append(plain, 0, marker);
            if (e > s) text.Append(plain, marker + s, e - s);
            if (to > start + laidOut) text.Append(Separator(entry));
        }
        return text.ToString();
    }

    public static async Task CopyAsync(MarkdownViewer viewer) {
        var text = SelectedText(viewer);
        if (text.Length == 0 || TopLevel.GetTopLevel(viewer)?.Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(text);
    }

    static void OnPressed(MarkdownViewer viewer, PointerPressedEventArgs e) {
        if (!e.GetCurrentPoint(viewer).Properties.IsLeftButtonPressed) return;
        if (LayerOf(viewer) is not Visual layer) return;
        var point = e.GetPosition(layer);
        if (e.ClickCount == 2) {
            if (HitTestEntry(layer, point) is not { } entry || HitTestOffset(layer, point) is not { } offset) return;
            var start = AbsStart(entry);
            var plain = PlainText(entry);
            var (from, to) = LineSelection.LineBounds(plain[MarkerLength(entry, plain)..], offset - start);
            Anchor(layer) = start + from;
            Focus(layer) = start + to;
            layer.InvalidateVisual();
            return;
        }
        if (e.ClickCount != 1 || HitTestOffset(layer, point) is not null || PressedOnControl(e)) return;
        // Padding sits outside the glyph bounds, so MarkView records no anchor and the drag never starts.
        if (LeftCodePadding(layer, point) is not { } inside || HitTestOffset(layer, inside) is not { } snapped) return;
        Anchor(layer) = snapped;
        Focus(layer) = snapped;
        IsDragging(viewer) = true;
        layer.InvalidateVisual();
    }

    static bool PressedOnControl(PointerPressedEventArgs e) {
        for (var visual = e.Source as Visual; visual is not null; visual = visual.GetVisualParent())
            if (visual is Button or MarkdownImage { Target: not null }) return true;
        return false;
    }

    static Point? LeftCodePadding(Visual layer, Point point) {
        foreach (var entry in Entries(layer)) {
            var block = TextBlockOf(entry);
            if (block.GetVisualParent() is not Border border || !border.Classes.Contains("markdown-code-block")) continue;
            if (block.TranslatePoint(default, layer) is not { } origin || border.TranslatePoint(default, layer) is not { } edge) continue;
            var local = point - origin;
            if (point.X < edge.X + border.BorderThickness.Left || local.X >= block.Padding.Left) continue;
            var y = block.Padding.Top;
            foreach (var line in block.TextLayout.TextLines) {
                if (local.Y >= y && local.Y < y + line.Height)
                    return new Point(origin.X + block.Padding.Left, point.Y);
                y += line.Height;
            }
        }
        return null;
    }

    /// MarkView moves the selection's end only while the pointer is over a block, and the chat's
    /// blocks are exactly as wide as their text: leaving one past its last glyph keeps whatever
    /// the last sample inside hit, often the caret before that glyph.
    static void OnMoved(MarkdownViewer viewer, PointerEventArgs e) {
        if (!IsDragging(viewer) || LayerOf(viewer) is not Visual layer) return;
        var point = e.GetPosition(layer);
        if (HitTestOffset(layer, point) is not null) return;
        if (Nearest(layer, point) is { } inside) MoveFocus(layer, inside);
    }

    static void OnKeyDown(Func<Task> copy, KeyEventArgs e) {
        var hotkeys = Application.Current?.PlatformSettings?.HotkeyConfiguration;
        if (hotkeys is null || !hotkeys.Copy.Any(g => g.Matches(e))) return;
        _ = copy();
        e.Handled = true;
    }

    /// The point inside the nearest block, pulled in from whichever side the pointer left by.
    static Point? Nearest(Visual layer, Point point) {
        Point? best = null;
        var bestDistance = double.MaxValue;
        foreach (var entry in Entries(layer)) {
            var block = TextBlockOf(entry);
            if (block.TranslatePoint(default, layer) is not { } origin) continue;
            var bounds = new Rect(origin, block.Bounds.Size);
            var inside = new Point(Math.Clamp(point.X, bounds.Left, bounds.Right), Math.Clamp(point.Y, bounds.Top, bounds.Bottom));
            var gap = point - inside;
            var distance = gap.X * gap.X + gap.Y * gap.Y;
            if (distance < bestDistance) (best, bestDistance) = (inside, distance);
        }
        return best;
    }

    static int MarkerLength(object entry, string plain) {
        var block = TextBlockOf(entry);
        var laidOut = block.Inlines is { } inlines ? ExtractPlainText(null, inlines) : block.Text ?? "";
        return plain.Length > laidOut.Length && plain.EndsWith(laidOut, StringComparison.Ordinal) ? plain.Length - laidOut.Length : 0;
    }

    static FieldInfo Private(Type type, string name) =>
        type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic) ?? throw new MissingFieldException(type.FullName, name);

    static Visual? LayerOf(MarkdownViewer viewer) => (Visual?)LayerField.GetValue(viewer);

    static IList Entries(object layer) => (IList)EntriesField.GetValue(layer)!;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_isDragging")]
    static extern ref bool IsDragging(MarkdownViewer viewer);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_anchor")]
    static extern ref int? Anchor([UnsafeAccessorType(LayerType)] object layer);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_focus")]
    static extern ref int? Focus([UnsafeAccessorType(LayerType)] object layer);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "HitTestOffset")]
    static extern int? HitTestOffset([UnsafeAccessorType(LayerType)] object layer, Point point);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "HitTestEntry")]
    [return: UnsafeAccessorType(EntryType)]
    static extern object? HitTestEntry([UnsafeAccessorType(LayerType)] object layer, Point point);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "OnPointerMoved")]
    static extern void MoveFocus([UnsafeAccessorType(LayerType)] object layer, Point point);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_TextBlock")]
    static extern TextBlock TextBlockOf([UnsafeAccessorType(EntryType)] object entry);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_PlainText")]
    static extern string PlainText([UnsafeAccessorType(EntryType)] object entry);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Separator")]
    static extern string Separator([UnsafeAccessorType(EntryType)] object entry);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_AbsStart")]
    static extern int AbsStart([UnsafeAccessorType(EntryType)] object entry);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "ExtractPlainText")]
    static extern string ExtractPlainText(MarkdownSelectableTextBlock? type, InlineCollection inlines);
}
