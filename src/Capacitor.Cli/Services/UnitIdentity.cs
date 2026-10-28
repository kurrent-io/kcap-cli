using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Install;

namespace Capacitor.Cli.Services;

/// <summary>
/// Unit-identity resolution shared by <c>DaemonCommands</c>'s service verbs and
/// <see cref="ServiceVerify"/>'s gate: the daemon binary this install ships
/// (<see cref="ResolveDaemonBinary"/>) and the config.json path implied by a unit's baked
/// <c>KCAP_CONFIG_DIR</c> (<see cref="ConfigPathFromUnitEnv"/>). Neither method is itself a
/// decision — each caller wraps its own failure contract on top (UX-only fail-soft-to-null in
/// <c>DaemonCommands</c>, fail-closed to <c>StartGateReason.EvidenceUnreadable</c> in the gate).
/// </summary>
static class UnitIdentity {
    /// <summary>Resolve the kcap-daemon executable shipped alongside this binary.</summary>
    public static string? ResolveDaemonBinary() {
        var dir     = AppContext.BaseDirectory;
        var ext     = OperatingSystem.IsWindows() ? ".exe" : "";
        var sibling = Path.Combine(dir, $"kcap-daemon{ext}");

        return File.Exists(sibling) ? InstallPaths.Stabilize(sibling) : null;
    }

    /// <summary>Whether a unit's binary is <paramref name="daemonBinary"/>, by its version directory, the
    /// stable <c>current</c> path or a symlinked spelling alike. An unreadable unit binary (null) is not.
    /// Case counts outside Windows: a macOS volume can be case-sensitive, and calling two installs one
    /// would hide the drift this exists to report.</summary>
    public static bool SameDaemon(string? unitBinary, string daemonBinary) =>
        unitBinary is not null && string.Equals(Canonical(unitBinary), Canonical(daemonBinary),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    static string Canonical(string path) => PhysicalPath.Of(InstallPaths.Stabilize(Path.GetFullPath(path)));

    /// <summary>config.json path for a baked <c>KCAP_CONFIG_DIR</c>, or the default config root
    /// when the unit baked none.</summary>
    public static string ConfigPathFromUnitEnv(IReadOnlyDictionary<string, string> unitEnv, ConfigRoot config) =>
        AppConfig.GetConfigPath(
            unitEnv.TryGetValue(ConfigRoot.ConfigDirEnvVar, out var dir) && !string.IsNullOrEmpty(dir)
                ? new ConfigRoot(dir)
                : config);
}
