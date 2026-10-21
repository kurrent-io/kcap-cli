using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;

namespace Capacitor.App.Views;

/// Stacks the transcript and what trails it, then reserves room below so the scroll end puts the
/// latest user turn at the top of the viewport until its reply outgrows the screen.
public sealed class ChatTailPanel : Panel {
    double _viewportHeight;

    public ChatTailPanel() => EffectiveViewportChanged += (_, e) => {
        if (Math.Abs(e.EffectiveViewport.Height - _viewportHeight) < 0.5) return;
        _viewportHeight = e.EffectiveViewport.Height;
        InvalidateMeasure();
    };

    protected override Size MeasureOverride(Size availableSize) {
        var width = 0.0;
        var height = 0.0;
        double? pin = null;
        foreach (var child in Children) {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            if (child is ItemsPresenter { Panel: ChatVirtualizingPanel { LastUserTurnTop: { } top } } presenter)
                pin = height + presenter.Margin.Top + top;
            width = Math.Max(width, child.DesiredSize.Width);
            height += child.DesiredSize.Height;
        }
        var reserve = pin is { } at ? Math.Max(0, _viewportHeight - (height - at)) : 0;
        return new Size(width, height + reserve);
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
