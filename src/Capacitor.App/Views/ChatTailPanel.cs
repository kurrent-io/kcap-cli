using Avalonia;
using Avalonia.Controls;

namespace Capacitor.App.Views;

public sealed class ChatTailPanel : Panel {
    protected override Size MeasureOverride(Size availableSize) {
        var width = 0.0;
        var height = 0.0;
        foreach (var child in Children) {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            width = Math.Max(width, child.DesiredSize.Width);
            height += child.DesiredSize.Height;
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize) {
        var y = 0.0;
        foreach (var child in Children) {
            child.Arrange(new Rect(0, y, finalSize.Width, child.DesiredSize.Height));
            y += child.DesiredSize.Height;
        }
        return finalSize;
    }
}
