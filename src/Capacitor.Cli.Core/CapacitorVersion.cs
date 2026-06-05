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
    public static string CurrentDisplay() {
        var v    = Current();
        var plus = v.IndexOf('+');
        return plus >= 0 ? v[..plus] : v;
    }
}
