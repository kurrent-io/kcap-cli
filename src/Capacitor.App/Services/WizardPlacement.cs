using Avalonia;

namespace Capacitor.App.Services;

/// Where setup's window opens and how small it may get. Sizes are device-independent units measured
/// at the screen's own scaling, so 760 units take 950 pixels at 125% and 1140 at 150%: on a laptop
/// that is more than the work area holds, and the window would open with its footer under the taskbar.
public static class WizardPlacement {
    public const double PreferredWidth = 760;
    public const double PreferredHeight = 760;

    /// The preferred size, grown to the content scale's minimum, then shrunk into the screen's work
    /// area and centred on it.
    public static (PixelPoint Position, Size Size) Place(double contentScale, ScreenArea screen) {
        var minimum = Minimum(contentScale, screen.WorkingArea.Size.ToSize(screen.Scaling));
        var preferred = new Size(Math.Max(PreferredWidth, minimum.Width), Math.Max(PreferredHeight, minimum.Height));
        var centred = screen.Centre(preferred);

        return WindowRestore.FitOnScreen(centred, preferred, [screen]) ?? (centred, preferred);
    }

    /// Never larger than the work area: a minimum the screen cannot hold pushes the footer off it.
    public static Size Minimum(double contentScale, Size? available) => WindowSizeMemory.Minimum(
        new Size(WindowSizeMemory.OnboardingMinWidth, WindowSizeMemory.OnboardingMinHeight), contentScale, available);
}
