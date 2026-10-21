using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Capacitor.App.Views;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit;

/// The session-switch still is a bitmap of the content pane. On a 2× framebuffer that bitmap
/// has twice the pixels of its DIP size; a mark at the pane's center has to stay in the center
/// of the cover.
[NotInParallel("AvaloniaSession")]
public class SwitchCoverSnapshotTests {
    [Test]
    [Arguments(1.0)]
    [Arguments(2.0)]
    public Task The_still_keeps_the_center_of_the_pane(double renderScaling) => RunOnUiAsync(async () => {
        var marker = new Border {
            Width = 32,
            Height = 32,
            Background = Brushes.Red,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var pane = new Panel { Background = Brushes.Black, Children = { marker } };
        var still = new CoverBitmap();
        var cover = new Border { Child = still, IsVisible = false, Background = Brushes.Black };
        var window = new Window {
            Width = 400,
            Height = 300,
            Background = Brushes.Black,
            Content = new Panel { Children = { pane, cover } },
        };
        try {
            window.Show();
            window.SetRenderScaling(renderScaling);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();

            var size = pane.Bounds.Size;
            await Assert.That(size.Width).IsGreaterThan(100);
            await Assert.That(size.Height).IsGreaterThan(100);

            var scale = TopLevel.GetTopLevel(pane)!.RenderScaling;
            var bitmap = new RenderTargetBitmap(PixelSize.FromSize(size, scale), new Vector(96 * scale, 96 * scale));
            bitmap.Render(pane);
            var rendered = Sample(bitmap, bitmap.PixelSize.Width / 2, bitmap.PixelSize.Height / 2);

            still.Source = bitmap;
            cover.IsVisible = true;
            pane.IsVisible = false;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();

            var frame = window.CaptureRenderedFrame()!;
            var shown = Sample(frame, frame.PixelSize.Width / 2, frame.PixelSize.Height / 2);
            var corner = Sample(frame, frame.PixelSize.Width - 8, frame.PixelSize.Height - 8);
            await Assert.That(rendered).IsEqualTo("rgba(255,0,0,255)");
            await Assert.That(shown).IsEqualTo("rgba(255,0,0,255)");
            await Assert.That(corner).IsEqualTo("rgba(0,0,0,255)");
        } finally {
            window.Close();
        }
    });

    static string Sample(Bitmap bitmap, int x, int y) {
        if (bitmap is not WriteableBitmap writable)
            return Copy(bitmap, x, y);
        using var fb = writable.Lock();
        return Read(fb.Address, fb.RowBytes, fb.Format, x, y);
    }

    static string Copy(Bitmap bitmap, int x, int y) {
        var pixel = new byte[4];
        var pin = GCHandle.Alloc(pixel, GCHandleType.Pinned);
        try {
            bitmap.CopyPixels(new PixelRect(x, y, 1, 1), pin.AddrOfPinnedObject(), pixel.Length, pixel.Length);
            return Read(pin.AddrOfPinnedObject(), pixel.Length, bitmap.Format ?? PixelFormat.Rgba8888, 0, 0);
        } finally {
            pin.Free();
        }
    }

    static string Read(IntPtr address, int rowBytes, PixelFormat format, int x, int y) {
        var bpp = format.BitsPerPixel / 8;
        var at = address + y * rowBytes + x * bpp;
        var b0 = Marshal.ReadByte(at);
        var b1 = Marshal.ReadByte(at, 1);
        var b2 = Marshal.ReadByte(at, 2);
        var b3 = bpp > 3 ? Marshal.ReadByte(at, 3) : (byte)255;
        return format == PixelFormat.Rgba8888
            ? $"rgba({b0},{b1},{b2},{b3})"
            : $"{format}({b0},{b1},{b2},{b3})";
    }
}
