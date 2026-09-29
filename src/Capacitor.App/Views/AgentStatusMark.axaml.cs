using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Capacitor.App.Views;

public partial class AgentStatusMark : UserControl {
    public AgentStatusMark() {
        InitializeComponent();
        // The class arrives from the parent's XAML after this constructor.
        Classes.CollectionChanged += (_, _) => ApplyHeaderMetrics();
    }

    protected override void OnLoaded(RoutedEventArgs e) {
        base.OnLoaded(e);
        ApplyHeaderMetrics();
    }

    /// The checkout path is 13px, weight Normal. A local line height of NaN beats the rail's 16px
    /// style: a line shorter than the font clamps the glyphs, which shears "Working".
    void ApplyHeaderMetrics() {
        if (!Classes.Contains("header")) return;
        StatusWord.FontSize = 13;
        StatusWord.FontWeight = FontWeight.Normal;
        StatusWord.LineHeight = double.NaN;
        Glyph.Width = 16;
        Glyph.Height = 16;
        // The ring is square on the line box, so its centre sits under the caps. The same
        // translateY(-1.5px) an icon beside a label uses.
        Glyph.RenderTransform = new TranslateTransform(0, -1.5);
    }
}
