using Avalonia;

namespace Capacitor.App;

/// The macOS renderer order, overridable through KCAP_APP_RENDERER (metal, opengl, software) so a
/// renderer-specific defect can be compared on one build. An unknown value keeps the default.
public static class MacRenderingOrder
{
    public const string EnvVar = "KCAP_APP_RENDERER";

    public static AvaloniaNativeRenderingMode[] Resolve(string? value) =>
        value?.Trim().ToLowerInvariant() switch {
            "opengl"   => [AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software],
            "software" => [AvaloniaNativeRenderingMode.Software],
            _          => [AvaloniaNativeRenderingMode.Metal, AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software],
        };
}
