using Avalonia;
using Avalonia.Controls;

namespace Capacitor.App.Services;

/// Hides a window to the tray and shows it again in the state it left. Neither desktop backend does
/// that on its own: Win32 reshows a hidden window in the state last requested through WindowState,
/// which a maximize the OS performed (caption double-click, snap, Win+Up) never updates, and an X11
/// window manager drops the maximized atoms when the window is unmapped. So the state is read before
/// hiding and requested again before showing, and a normal frame is pulled inside the work area of
/// the screen it comes back on, whose size or scaling may have changed while it was hidden.
public sealed class WindowRestore {
    WindowState _lastShown = WindowState.Normal;
    WindowState? _hiddenAs;

    /// Follows the window's state while it is on screen, so one closed while minimized comes back
    /// as it was before that. A hidden window's state changes are the platform's, not the user's.
    public void Track(Window window) {
        if (window.WindowState != WindowState.Minimized) _lastShown = window.WindowState;
        window.PropertyChanged += (_, e) => {
            if (e.Property != Window.WindowStateProperty || !window.IsVisible) return;
            if (e.NewValue is WindowState state && state != WindowState.Minimized) _lastShown = state;
        };
    }

    public void Hide(Window window) {
        if (window.IsVisible) _hiddenAs = window.WindowState == WindowState.Minimized ? _lastShown : window.WindowState;
        window.Hide();
    }

    /// Call before Show(). A window this never hid is only brought back from Minimized.
    public void BeforeShow(Window window) {
        if (_hiddenAs is not { } state) {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            return;
        }

        _hiddenAs = null;
        // A minimized frame sits at the platform's parking position, not where it will reappear.
        if (state == WindowState.Normal && window.WindowState == WindowState.Normal) PullOnScreen(window);
        window.WindowState = state;
    }

    static void PullOnScreen(Window window) {
        if (double.IsNaN(window.Width) || double.IsNaN(window.Height)) return;
        if (FitOnScreen(window.Position, new Size(window.Width, window.Height), ScreenAreas(window)) is not { } fit) return;
        window.Position = fit.Position;
        window.Width = fit.Size.Width;
        window.Height = fit.Size.Height;
    }

    /// Null when the frame already lies inside the work area of the screen holding most of it.
    /// Otherwise it shrinks to that work area and moves inside it; a frame on no screen goes to the
    /// first one. The size is in device-independent units, measured at each screen's own scaling.
    public static (PixelPoint Position, Size Size)? FitOnScreen(PixelPoint position, Size size, IReadOnlyList<ScreenArea> screens) {
        if (screens.Count == 0) return null;

        var target = screens[0];
        long best = 0;
        foreach (var screen in screens) {
            var overlap = Area(Frame(position, size, screen.Scaling).Intersect(screen.Bounds));
            if (overlap <= best) continue;
            best = overlap;
            target = screen;
        }

        var area = target.WorkingArea;
        var frame = Frame(position, size, target.Scaling);
        var width = Math.Min(frame.Width, area.Width);
        var height = Math.Min(frame.Height, area.Height);
        var fitted = new PixelRect(
            Math.Clamp(frame.X, area.X, area.Right - width),
            Math.Clamp(frame.Y, area.Y, area.Bottom - height),
            width, height);
        if (fitted == frame) return null;

        return (fitted.Position, new Size(
            width == frame.Width ? size.Width : width / target.Scaling,
            height == frame.Height ? size.Height : height / target.Scaling));
    }

    // Primary first: a frame on no screen lands there.
    public static IReadOnlyList<ScreenArea> ScreenAreas(Window window) {
        if (window.Screens is not { All: { Count: > 0 } all } screens) return [];
        var areas = new List<ScreenArea>(all.Count);
        if (screens.Primary is { } primary) areas.Add(new ScreenArea(primary.Bounds, primary.WorkingArea, primary.Scaling));
        foreach (var screen in all) {
            var area = new ScreenArea(screen.Bounds, screen.WorkingArea, screen.Scaling);
            if (!areas.Contains(area)) areas.Add(area);
        }

        return areas;
    }

    static PixelRect Frame(PixelPoint position, Size size, double scaling) =>
        new(position, new PixelSize((int)Math.Ceiling(size.Width * scaling), (int)Math.Ceiling(size.Height * scaling)));

    static long Area(PixelRect rect) => (long)rect.Width * rect.Height;
}
