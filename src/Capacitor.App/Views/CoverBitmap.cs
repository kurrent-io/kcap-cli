using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Capacitor.App.Views;

/// Draws a bitmap from its pixels. <see cref="Image"/> takes the source rect from
/// <see cref="Bitmap.Size"/>, the DIP size, so a still rendered at the window's render scale
/// shows only the top-left of the buffer.
public class CoverBitmap : Control {
    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<CoverBitmap, Bitmap?>(nameof(Source));

    static CoverBitmap() => AffectsRender<CoverBitmap>(SourceProperty);

    public Bitmap? Source {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public override void Render(DrawingContext context) {
        if (Source is not { } source || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        context.DrawImage(source,
            new Rect(0, 0, source.PixelSize.Width, source.PixelSize.Height),
            new Rect(Bounds.Size));
    }
}
