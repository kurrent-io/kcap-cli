using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Capacitor.App.Controls;

/// Glass over the flat canvas refracts nothing, so the shell paints something behind both panes.
/// Static on purpose: following the pointer re-captures the window and redraws every glass surface.
public sealed class MaterialBackdrop : Control {
    public static readonly StyledProperty<double> RailWidthProperty =
        AvaloniaProperty.Register<MaterialBackdrop, double>(nameof(RailWidth), 334);

    static readonly IBrush RailTeal = GlowBrush(Color.Parse("#553D7581"));
    static readonly IBrush RailViolet = GlowBrush(Color.Parse("#344D4878"));
    static readonly IBrush PaneGreen = GlowBrush(Color.Parse("#8023806C"));
    static readonly IBrush PaneViolet = GlowBrush(Color.Parse("#6851528F"));
    static readonly IBrush PaneBlue = GlowBrush(Color.Parse("#45226B85"));

    static MaterialBackdrop() {
        AffectsRender<MaterialBackdrop>(RailWidthProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<MaterialBackdrop>(false);
    }

    public double RailWidth {
        get => GetValue(RailWidthProperty);
        set => SetValue(RailWidthProperty, value);
    }

    public override void Render(DrawingContext context) {
        var l = RailWidth;
        var w = Bounds.Width - l;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        Glow(context, RailTeal, new Point(l * 0.35, h * 0.42), l * 1.25, h * 0.7);
        Glow(context, RailViolet, new Point(l * 0.5, h * 0.85), l, h * 0.45);
        Glow(context, PaneGreen, new Point(l + w * 0.3, h * 0.55), w * 0.43, h * 0.46);
        Glow(context, PaneViolet, new Point(l + w * 0.72, h * 0.59), w * 0.38, h * 0.4);
        Glow(context, PaneBlue, new Point(l + w * 0.55, h * 0.35), w * 0.36, h * 0.33);
    }

    static void Glow(DrawingContext context, IBrush brush, Point center, double rx, double ry) =>
        context.DrawEllipse(brush, null, center, rx, ry);

    static IBrush GlowBrush(Color tint) => new RadialGradientBrush {
        GradientStops = { new GradientStop(tint, 0), new GradientStop(Color.FromArgb(0, tint.R, tint.G, tint.B), 1) },
    }.ToImmutable();
}
