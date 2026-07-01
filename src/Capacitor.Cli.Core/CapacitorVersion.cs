using System.Reflection;

namespace Capacitor.Cli.Core;

/// <summary>
/// Single source of truth for the version string stamped into installer
/// marker files (skills, codex hooks, claude plugin, …). Every installer
/// MUST call this so a build's markers stay consistent — a same-version
/// short-circuit check elsewhere assumes all markers carry the same value.
/// </summary>
public static class CapacitorVersion {
    public static string Current() =>
        typeof(CapacitorVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";

    /// <summary>
    /// Same value as <see cref="Current"/> but with the <c>+buildmetadata</c>
    /// suffix stripped. Use this for any version string shown to a human
    /// (CLI stderr hints, in-agent upgrade nudges) so the raw commit SHA from
    /// MinVer's <see cref="AssemblyInformationalVersionAttribute"/> doesn't
    /// leak into user-facing output. Use <see cref="Current"/> for version
    /// markers and other machine-consumed identifiers that must match across
    /// installers exactly.
    /// </summary>
    public static string CurrentDisplay() => Display(Current());

    /// <summary>
    /// Strips the <c>+buildmetadata</c> suffix from an arbitrary version string
    /// so the raw commit SHA from MinVer's semver doesn't leak into user-facing
    /// output. Used for <see cref="CurrentDisplay"/> and to render another
    /// process's reported version (e.g. the daemon's, on <c>kcap daemon status</c>).
    /// </summary>
    public static string Display(string version) {
        var plus = version.IndexOf('+');
        return plus >= 0 ? version[..plus] : version;
    }
}
