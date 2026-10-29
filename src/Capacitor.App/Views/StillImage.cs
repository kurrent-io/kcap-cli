using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Capacitor.App.Views;

/// A bitmap stretched over the control's bounds. `Image` hands the renderer a source rect in DIPs,
/// which Skia reads as pixels, so a still taken above 96 DPI would show its top-left corner magnified.
public sealed class StillImage : Control {
    Bitmap? _source;

    public Bitmap? Source {
        get => _source;
        set {
            _source = value;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context) {
        if (_source is { } source)
            context.DrawImage(source, new Rect(source.PixelSize.ToSize(1)), new Rect(Bounds.Size));
    }
}
