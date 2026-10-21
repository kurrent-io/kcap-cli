using Avalonia;

namespace Capacitor.App.Platform.Linux;

/// The X11 renderer order, overridable through KCAP_APP_RENDERER (glx, egl, vulkan, software) so a
/// renderer-specific defect can be compared on one build. An unknown value keeps the default.
public static class LinuxRenderingOrder
{
    public const string EnvVar = "KCAP_APP_RENDERER";

    public static X11RenderingMode[] Resolve(string? value) =>
        value?.Trim().ToLowerInvariant() switch {
            "egl"      => [X11RenderingMode.Egl, X11RenderingMode.Software],
            "vulkan"   => [X11RenderingMode.Vulkan, X11RenderingMode.Software],
            "software" => [X11RenderingMode.Software],
            _          => [X11RenderingMode.Glx, X11RenderingMode.Software],
        };
}
