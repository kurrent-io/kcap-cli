using Avalonia;
using Avalonia.Controls;

namespace Capacitor.App.Controls;

/// A card. CornerRadius is the site's and governs the opaque template only; the glass radius is a
/// material parameter, set by class styles and never at a site, where a local value would win.
public sealed class Surface : ContentControl {
    public static readonly StyledProperty<CornerRadius> GlassCornerRadiusProperty =
        AvaloniaProperty.Register<Surface, CornerRadius>(nameof(GlassCornerRadius), new CornerRadius(18));

    public static readonly StyledProperty<GlassKind> GlassKindProperty =
        AvaloniaProperty.Register<Surface, GlassKind>(nameof(GlassKind));

    public CornerRadius GlassCornerRadius {
        get => GetValue(GlassCornerRadiusProperty);
        set => SetValue(GlassCornerRadiusProperty, value);
    }

    public GlassKind GlassKind {
        get => GetValue(GlassKindProperty);
        set => SetValue(GlassKindProperty, value);
    }
}
