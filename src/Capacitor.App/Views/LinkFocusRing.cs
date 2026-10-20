using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Capacitor.App.Views;

/// The keyboard focus mark on a link: its text bounds inside a text block, or a linked image's own bounds.
/// They are read on every render, and the host's layout passes trigger one, so a re-wrap moves the mark
/// with the text.
sealed class LinkFocusRing : Control {
    readonly Control _block;
    readonly Func<IReadOnlyList<Rect>> _rects;

    public LinkFocusRing(Control block, Func<IReadOnlyList<Rect>> rects) {
        _block = block;
        _rects = rects;
        IsHitTestVisible = false;
    }

    internal IReadOnlyList<Rect> Rects => _rects();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        _block.LayoutUpdated += OnBlockLayoutUpdated;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        _block.LayoutUpdated -= OnBlockLayoutUpdated;
        base.OnDetachedFromVisualTree(e);
    }

    void OnBlockLayoutUpdated(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context) {
        var brush = this.TryFindResource("KcapInfoBrush", out var found) && found is IBrush b ? b : Brushes.CornflowerBlue;
        var pen = new Pen(brush, 1.5);
        foreach (var rect in _rects()) context.DrawRectangle(null, pen, rect.Inflate(new Thickness(2, 1)), 3, 3);
    }
}
