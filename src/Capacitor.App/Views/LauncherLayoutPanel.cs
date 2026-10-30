using Avalonia;
using Avalonia.Controls;

namespace Capacitor.App.Views;

/// The launcher's two layers: notices pinned to the top, then the composer centred in the whole
/// pane. A notice that would reach the composer pushes it down instead of covering it, so the
/// composer only moves when it has to.
public sealed class LauncherLayoutPanel : Panel {
    public static readonly StyledProperty<double> GapProperty =
        AvaloniaProperty.Register<LauncherLayoutPanel, double>(nameof(Gap), 16);

    static LauncherLayoutPanel() => AffectsMeasure<LauncherLayoutPanel>(GapProperty);

    public double Gap {
        get => GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) {
        var width = 0.0;
        foreach (var child in Children) {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            width = Math.Max(width, child.DesiredSize.Width);
        }
        var (top, content) = Split();
        return new Size(width, NoticeHeight(top) + (content?.DesiredSize.Height ?? 0));
    }

    protected override Size ArrangeOverride(Size finalSize) {
        var (top, content) = Split();
        top?.Arrange(new Rect(0, 0, finalSize.Width, top.DesiredSize.Height));
        if (content is not null) {
            var height = content.DesiredSize.Height;
            var y = Math.Max((finalSize.Height - height) / 2, NoticeHeight(top));
            content.Arrange(new Rect(0, y, finalSize.Width, height));
        }
        return finalSize;
    }

    (Control? Top, Control? Content) Split() =>
        (Children.Count > 0 ? Children[0] : null, Children.Count > 1 ? Children[1] : null);

    double NoticeHeight(Control? top) =>
        top is { IsVisible: true, DesiredSize.Height: > 0 } ? top.DesiredSize.Height + Gap : 0;
}
