namespace Capacitor.App.Materials;

public enum SurfaceMaterial { Opaque, SoftGlass, LiquidGlass }

public static class SurfaceMaterials {
    public static string ToStored(this SurfaceMaterial material) => material switch {
        SurfaceMaterial.SoftGlass => "soft_glass",
        SurfaceMaterial.LiquidGlass => "liquid_glass",
        _ => "opaque",
    };

    /// Null for anything unrecognised: a value written by a newer build reads as "no choice".
    public static SurfaceMaterial? Parse(string? stored) => stored switch {
        "opaque" => SurfaceMaterial.Opaque,
        "soft_glass" => SurfaceMaterial.SoftGlass,
        "liquid_glass" => SurfaceMaterial.LiquidGlass,
        _ => null,
    };

    public static bool IsGlass(this SurfaceMaterial material) => material != SurfaceMaterial.Opaque;
}
