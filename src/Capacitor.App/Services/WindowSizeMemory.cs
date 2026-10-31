using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Capacitor.App.Services;

public static class WindowSizeMemory {
    public const double DefaultWidth = 1400;
    public const double DefaultHeight = 760;
    public const double MinWidth = 1200;
    public const double MinHeight = 560;
    public const double OnboardingMinWidth = 720;
    public const double OnboardingMinHeight = 600;

    static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(400);

    public static (double Width, double Height) Resolve(double? savedWidth, double? savedHeight) =>
        (Fit(savedWidth, MinWidth, DefaultWidth), Fit(savedHeight, MinHeight, DefaultHeight));

    /// The remembered size and position, or the default size centred on the first screen. A
    /// remembered frame the work areas hold whole is kept, straddling monitors included; any other
    /// frame is pulled whole into the work area of the screen holding most of it, since one taller
    /// than that area opens with its bottom edge under the taskbar. The position is null only with no screen.
    public static (PixelPoint? Position, Size Size) Place(AppState state, IReadOnlyList<ScreenArea> screens) {
        var (width, height) = Resolve(state.WindowWidth, state.WindowHeight);
        var size = new Size(width, height);
        PixelPoint? saved = state.WindowX is int x && state.WindowY is int y ? new PixelPoint(x, y) : null;
        if (screens.Count == 0) return (saved, size);
        if (saved is { } kept && WindowRestore.InsideWorkAreas(kept, size, screens)) return (kept, size);

        var position = saved ?? screens[0].Centre(size);
        return WindowRestore.FitOnScreen(position, size, screens) is { } fit ? (fit.Position, fit.Size) : (position, size);
    }

    /// A content scale's minimum, never larger than the work area: the platform holds a window at
    /// its minimum, so one the screen cannot hold keeps the frame past the screen's edge.
    public static Size Minimum(Size unscaled, double contentScale, Size? available) => new(
        Bounded(unscaled.Width, contentScale, available?.Width),
        Bounded(unscaled.Height, contentScale, available?.Height));

    static double Bounded(double unscaled, double scale, double? available) =>
        Math.Min(UiScale.Minimum(unscaled, scale, available), available ?? double.MaxValue);

    public static void Apply(Window window, AppState state) {
        var (position, size) = Place(state, WindowRestore.ScreenAreas(window));
        window.Width = size.Width;
        window.Height = size.Height;
        if (position is { } at) {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = at;
        }
        if (state.WindowMaximized) window.WindowState = WindowState.Maximized;
    }

    public static void Attach(Window window, IAppStateStore store) {
        var lastWidth = window.Width;
        var lastHeight = window.Height;
        var lastPos = window.Position;
        // Minimized is transient: a window minimized from Maximized should come back maximized.
        var lastShown = window.WindowState;
        var quiet = new DispatcherTimer { Interval = Quiet };

        void Snapshot() {
            lastWidth = window.Width;
            lastHeight = window.Height;
            lastPos = window.Position;
        }

        // A hidden window's geometry and state are the platform's: an X11 window manager drops the
        // maximized state on unmap, and the tray restore moves the frame before showing it.
        void RememberNormal() {
            if (window.WindowState != WindowState.Normal || !window.IsVisible) return;
            Snapshot();
        }

        void Schedule() {
            quiet.Stop();
            quiet.Start();
        }

        void Flush() {
            quiet.Stop();
            RememberNormal();
            store.UpdateAsync(s => s with {
                WindowWidth = lastWidth,
                WindowHeight = lastHeight,
                WindowX = lastPos.X,
                WindowY = lastPos.Y,
                WindowMaximized = lastShown == WindowState.Maximized,
            }).GetAwaiter().GetResult();
        }

        void OnMovedOrResized() {
            RememberNormal();
            Schedule();
        }

        quiet.Tick += (_, _) => Flush();
        window.Resized += (_, _) => OnMovedOrResized();
        window.PositionChanged += (_, _) => OnMovedOrResized();
        // Avalonia has no RestoreBounds; leaving Normal is the last moment size and position are
        // still the restore geometry, so snapshot before the maximized frame lands.
        window.PropertyChanged += (_, e) => {
            if (e.Property != Window.WindowStateProperty || !window.IsVisible) return;
            if (e.OldValue is WindowState.Normal) Snapshot();
            if (e.NewValue is WindowState state && state != WindowState.Minimized) lastShown = state;
            Schedule();
        };
        window.Closing += (_, _) => Flush();
    }

    static double Fit(double? saved, double min, double fallback) {
        if (saved is not double value || !double.IsFinite(value) || value <= 0) return fallback;
        return Math.Max(min, value);
    }
}
