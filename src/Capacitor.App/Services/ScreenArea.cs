using Avalonia;

namespace Capacitor.App.Services;

/// One monitor as window placement sees it: its bounds and work area in physical pixels, and its scaling.
public readonly record struct ScreenArea(PixelRect Bounds, PixelRect WorkingArea, double Scaling) {
    /// The top-left that centres a frame of this device-independent size on the work area.
    public PixelPoint Centre(Size size) {
        var width = (int)Math.Ceiling(size.Width * Scaling);
        var height = (int)Math.Ceiling(size.Height * Scaling);
        return new PixelPoint(WorkingArea.X + (WorkingArea.Width - width) / 2, WorkingArea.Y + (WorkingArea.Height - height) / 2);
    }
}
