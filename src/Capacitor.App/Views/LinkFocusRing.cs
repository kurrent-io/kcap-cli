using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Capacitor.App.Views;

/// The keyboard focus mark on a link inside a text block, drawn over the link's text bounds.
sealed class LinkFocusRing : Control {
    readonly IReadOnlyList<Rect> _rects;

    public LinkFocusRing(IReadOnlyList<Rect> rects) {
        _rects = rects;
        IsHitTestVisible = false;
    }

    public override void Render(DrawingContext context) {
        var brush = this.TryFindResource("KcapInfoBrush", out var found) && found is IBrush b ? b : Brushes.CornflowerBlue;
        var pen = new Pen(brush, 1.5);
        foreach (var rect in _rects) context.DrawRectangle(null, pen, rect.Inflate(new Thickness(2, 1)), 3, 3);
    }
}
