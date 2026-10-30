using Avalonia;

namespace Capacitor.App.Services;

/// One monitor as window placement sees it: its bounds and work area in physical pixels, and its scaling.
public readonly record struct ScreenArea(PixelRect Bounds, PixelRect WorkingArea, double Scaling);
