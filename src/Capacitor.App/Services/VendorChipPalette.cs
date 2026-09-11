using System.Collections.Frozen;

namespace Capacitor.App.Services;

/// Per-vendor chip colours, mirroring the web UI's vendor chips. The desktop app paints a fixed
/// dark surface, so these are literal hex rather than theme-swapped brushes. cursor and any vendor
/// not listed fall back to the neutral surface pair (KcapSurfaceRaised / KcapFaint).
public static class VendorChipPalette {
    public readonly record struct ChipColors(string Background, string Foreground);

    static readonly ChipColors Neutral = new("#191D27", "#949BAA");

    static readonly FrozenDictionary<string, ChipColors> Map =
        new Dictionary<string, ChipColors>(StringComparer.OrdinalIgnoreCase) {
            ["claude"] = new("#C87B3A", "#1E1E1E"),
            ["codex"] = new("#10A37F", "#1E1E1E"),
            ["copilot"] = new("#8957E5", "#F5F1FB"),
            ["gemini"] = new("#1A73E8", "#FFFFFF"),
            ["kiro"] = new("#1E293B", "#E2E8F0"),
            ["pi"] = new("#E64980", "#FFF0F6"),
            ["opencode"] = new("#DC2626", "#FEF2F2"),
            ["antigravity"] = new("#34A853", "#FFFFFF"),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static ChipColors For(string? vendor) =>
        vendor is not null && Map.TryGetValue(vendor, out var colors) ? colors : Neutral;
}
