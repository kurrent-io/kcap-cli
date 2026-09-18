using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Capacitor.App.Services;

public static class WindowSizeMemory {
    public const double DefaultWidth = 1400;
    public const double DefaultHeight = 760;
    public const double MinWidth = 1200;
    public const double MinHeight = 560;

    static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(400);

    public static (double Width, double Height) Resolve(double? savedWidth, double? savedHeight) =>
        (Fit(savedWidth, MinWidth, DefaultWidth), Fit(savedHeight, MinHeight, DefaultHeight));

    /// A top-left still on some screen is kept as saved, straddling monitors included. Off every
    /// screen, the whole frame is pulled onto the first area; a frame larger than it keeps its
    /// top-left edge.
    public static PixelPoint? ResolvePosition(int? savedX, int? savedY, PixelSize size, IReadOnlyList<PixelRect> workingAreas) {
        if (savedX is not int x || savedY is not int y) return null;
        var pos = new PixelPoint(x, y);
        if (workingAreas.Count == 0) return pos;
        foreach (var area in workingAreas) {
            if (area.Contains(pos)) return pos;
        }

        var home = workingAreas[0];
        return new PixelPoint(ClampOnto(x, home.X, home.Width, size.Width), ClampOnto(y, home.Y, home.Height, size.Height));
    }

    public static void Restore(Window window, IAppStateStore store) =>
        Apply(window, store.LoadAsync().GetAwaiter().GetResult());

    public static void Apply(Window window, AppState state) {
        var (width, height) = Resolve(state.WindowWidth, state.WindowHeight);
        window.Width = width;
        window.Height = height;
        var scaling = window.Screens?.Primary?.Scaling ?? 1;
        var size = new PixelSize((int)Math.Ceiling(width * scaling), (int)Math.Ceiling(height * scaling));
        if (ResolvePosition(state.WindowX, state.WindowY, size, WorkingAreas(window)) is { } pos)
            window.Position = pos;
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

        void RememberNormal() {
            if (window.WindowState != WindowState.Normal) return;
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
            if (e.Property != Window.WindowStateProperty) return;
            if (e.OldValue is WindowState.Normal) Snapshot();
            if (e.NewValue is WindowState state && state != WindowState.Minimized) lastShown = state;
            Schedule();
        };
        window.Closing += (_, _) => Flush();
    }

    // Primary first: off-screen restore clamps onto workingAreas[0].
    static IReadOnlyList<PixelRect> WorkingAreas(Window window) {
        var screens = window.Screens;
        if (screens?.All is not { Count: > 0 } all) return [];
        var areas = new List<PixelRect>(all.Count);
        if (screens.Primary is { } primary) areas.Add(primary.WorkingArea);
        foreach (var screen in all) {
            var area = screen.WorkingArea;
            if (!areas.Contains(area)) areas.Add(area);
        }

        return areas;
    }

    static int ClampOnto(int value, int origin, int length, int extent) =>
        Math.Clamp(value, origin, Math.Max(origin, origin + length - extent));

    static double Fit(double? saved, double min, double fallback) {
        if (saved is not double value || !double.IsFinite(value) || value <= 0) return fallback;
        return Math.Max(min, value);
    }
}
