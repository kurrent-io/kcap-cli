using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Services;

/// <summary>The search path a service unit carries: captured once at install, so a harness installed
/// into a directory the installing shell did not search stays invisible to the daemon.</summary>
static class ServicePath {
    /// <summary>
    /// <paramref name="current"/> followed by the directories only <paramref name="baked"/> has, when a
    /// harness CLI resolves on <paramref name="current"/> and not on <paramref name="baked"/>; null
    /// otherwise. The unit's own directories are kept so the rewrite never hides a CLI the daemon found.
    /// </summary>
    public static string? Refreshed(string? baked, string? current, HarnessRegistry harnesses) {
        if (string.IsNullOrEmpty(current)) return null;

        var before = harnesses.Searching(BinaryProbe.Searching(baked));
        var after  = harnesses.Searching(BinaryProbe.Searching(current));
        // The CLI only: detection also accepts IDE launchers, which no daemon can host.
        if (!after.Any(h => after.ResolveExecutable(h.Id) is not null && before.ResolveExecutable(h.Id) is null)) return null;

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        return string.Join(Path.PathSeparator, Directories(current).Concat(Directories(baked)).Distinct(comparer));

        static IEnumerable<string> Directories(string? path) =>
            (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
    }
}
