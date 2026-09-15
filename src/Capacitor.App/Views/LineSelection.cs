using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;

namespace Capacitor.App.Views;

/// Avalonia's TextBox and SelectableTextBlock answer a triple click with SelectAll; installed
/// once per application, this selects the logical line under the pointer instead.
public static class LineSelection {
    static bool _installed;

    public static void Install() {
        if (_installed) return;
        _installed = true;
        // Tunnel: both controls select everything in their own bubbling class handler, which a
        // handled tunnel event never reaches.
        TextBox.PointerPressedEvent.AddClassHandler<TextBox>(OnTextBoxPressed, RoutingStrategies.Tunnel);
        SelectableTextBlock.PointerPressedEvent.AddClassHandler<SelectableTextBlock>(OnBlockPressed, RoutingStrategies.Tunnel);
    }

    /// The line holding `index`, as a [start, end) range that excludes the newline on either side
    /// and a carriage return before the closing one. An index past the text lands on the last line.
    public static (int Start, int End) LineBounds(string text, int index) {
        index = Math.Clamp(index, 0, text.Length);
        var start = index == 0 ? 0 : text.LastIndexOf('\n', index - 1) + 1;
        var end = text.IndexOf('\n', index);
        if (end < 0) end = text.Length;
        if (end > start && text[end - 1] == '\r') end--;
        return (start, end);
    }

    static void OnTextBoxPressed(TextBox box, PointerPressedEventArgs e) {
        if (!IsTripleClick(e, box)) return;
        var presenter = box.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
        if (presenter?.TextLayout is not { } layout) return;
        var (start, end) = LineBounds(box.Text ?? "", IndexAt(layout, e.GetPosition(presenter)));
        box.SelectionStart = start;
        box.SelectionEnd = end;
        e.Handled = true;
    }

    static void OnBlockPressed(SelectableTextBlock block, PointerPressedEventArgs e) {
        if (!IsTripleClick(e, block)) return;
        var position = e.GetPosition(block) - new Point(block.Padding.Left, block.Padding.Top);
        var (start, end) = LineBounds(block.Text ?? "", IndexAt(block.TextLayout, position));
        block.SelectionStart = start;
        block.SelectionEnd = end;
        e.Handled = true;
    }

    static bool IsTripleClick(PointerPressedEventArgs e, Visual target) =>
        e.ClickCount == 3 && e.GetCurrentPoint(target).Properties.IsLeftButtonPressed;

    static int IndexAt(TextLayout layout, Point point) => layout.HitTestPoint(point).TextPosition;
}
