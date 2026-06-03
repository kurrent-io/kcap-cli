using System.Reflection;

namespace Capacitor.Cli.Core;

/// <summary>
/// Single source of truth for the version string stamped into installer
/// marker files (skills, codex hooks, claude plugin, …). Every installer
/// MUST call this so a build's markers stay consistent — a same-version
/// short-circuit check elsewhere assumes all markers carry the same value.
/// </summary>
public static class KapacitorVersion {
    public static string Current() =>
        typeof(KapacitorVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "unknown";
}
