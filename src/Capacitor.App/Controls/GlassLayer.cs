using Avalonia;
using Avalonia.Controls.Primitives;

namespace Capacitor.App.Controls;

/// The one place glass is drawn. Hosts place it behind their content and pass the radius and the
/// rim brush in; a Kind property rather than a class, because a template can bind a property.
public sealed class GlassLayer : TemplatedControl {
    public static readonly StyledProperty<GlassKind> KindProperty =
        AvaloniaProperty.Register<GlassLayer, GlassKind>(nameof(Kind));

    static GlassLayer() => IsHitTestVisibleProperty.OverrideDefaultValue<GlassLayer>(false);

    public GlassKind Kind {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }
}
