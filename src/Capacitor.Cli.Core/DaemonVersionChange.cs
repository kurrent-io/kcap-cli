namespace Capacitor.Cli.Core;

/// <summary>
/// What replacing a running daemon with another kcap's build does to its version, worded once so the
/// CLI and the app give the same advice. Each recommends a replace that installs its own build, so
/// without the direction an older kcap's advice is a silent downgrade.
/// </summary>
public static class DaemonVersionChange {
    /// <summary>True only when both versions parse and <paramref name="target"/> is the older one.</summary>
    public static bool IsDowngrade(string? running, string? target) => PrereleaseSemver.IsNewer(running, target);

    /// <summary>The versions a replace moves between, or null when either is unknown or both are the same release.</summary>
    public static string? Describe(string? running, string? target) {
        if (Known(running) is not { } from || Known(target) is not { } to || from == to) return null;

        return IsDowngrade(running, target)
            ? $"The daemon runs {from}, newer than this kcap's {to}: replacing it now downgrades it to {to}. Update this kcap to {from} or later first to keep the newer daemon."
            : $"The daemon runs {from}; replacing it installs {to}.";
    }

    static string? Known(string? version) =>
        string.IsNullOrWhiteSpace(version) || version.Trim() == "unknown" ? null : CapacitorVersion.Display(version.Trim());
}
