using Capacitor.App.Views;

namespace Capacitor.App.Materials;

public sealed record MaterialEnvironment(bool GlassCapable, bool ReduceTransparency) {
    /// Read once at startup. Glass is offered on macOS only: the one platform it was validated on.
    public static MaterialEnvironment Detect() =>
        new(OperatingSystem.IsMacOS(), AppKitAccessibility.ReduceTransparency());
}
