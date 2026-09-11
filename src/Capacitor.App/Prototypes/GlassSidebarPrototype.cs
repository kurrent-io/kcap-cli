using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Capacitor.App.Views;
using LiquidGlassAvaloniaUI;

namespace Capacitor.App.Prototypes;

// Reuses the real rail's tree, scrolling, footer and drag strip inside an inset glass panel.
// Current mode reparents the same content back into the original full-height rail.
sealed class GlassSidebarPrototype {
    readonly SessionRailView _rail;
    readonly Border _content;
    readonly Grid _chrome;
    readonly IBrush? _originalBackground;
    readonly Thickness _originalBorder;
    // The surface draws its directional highlight over this continuous, non-interactive rim.
    readonly Grid _glassContent = new() {
        Children = {
            new Border {
                CornerRadius = new CornerRadius(22),
                BorderBrush = Brush.Parse("#2EFFFFFF"),
                BorderThickness = new Thickness(1),
                IsHitTestVisible = false,
            },
        },
    };
    readonly LiquidGlassSurface _glass = new() {
        CornerRadius = new CornerRadius(22),
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch,
        RefractionHeight = 18,
        Vibrancy = 0.8,
        HighlightFalloff = 0.65,
        ShadowRadius = 20,
        ShadowOffset = new Vector(4, 8),
        ShadowColor = Color.Parse("#65000000"),
    };

    public GlassSidebarPrototype(SessionRailView rail) {
        _rail = rail;
        _content = (Border)rail.Content!;
        _chrome = (Grid)((StackPanel)((DockPanel)_content.Child!).Children[0]).Children[0];
        _originalBackground = _content.Background;
        _originalBorder = _content.BorderThickness;
        rail.Styles.Add(new StyleInclude(new Uri("avares://Kurrent Capacitor/")) {
            Source = new Uri("avares://Kurrent Capacitor/Prototypes/GlassSidebarStyles.axaml"),
        });
    }

    public void Apply(int mode) {
        _rail.Content = null;
        _glass.Content = null;
        _glassContent.Children.Remove(_content);
        _rail.Classes.Set("glassPrototypeRail", mode != 0);
        _rail.Margin = mode == 0 ? default : new Thickness(12, 40, 12, 12);
        _chrome.Height = mode == 0 ? 44 : 16;
        _content.Background = mode == 0 ? _originalBackground : Brushes.Transparent;
        _content.BorderThickness = mode == 0 ? _originalBorder : default;
        if (mode == 0) {
            _rail.Content = _content;
            return;
        }

        _glass.BlurRadius = mode == 1 ? 24 : 18;
        _glass.RefractionAmount = mode == 1 ? 5 : 14;
        _glass.TintColor = Color.Parse(mode == 1 ? "#344E667C" : "#284E667C");
        _glass.SurfaceColor = Color.Parse("#462F3745");
        _glass.HighlightOpacity = mode == 1 ? 0.28 : 0.42;
        _glass.HighlightWidth = mode == 1 ? 0.75 : 1;
        _glassContent.Children.Insert(0, _content);
        _glass.Content = _glassContent;
        _rail.Content = _glass;
    }
}
