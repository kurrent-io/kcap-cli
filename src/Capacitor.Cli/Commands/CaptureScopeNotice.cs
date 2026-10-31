using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Commands;

/// <summary>
/// The uninstall preview's account of the capture scope that goes with the config file. Each entry keeps
/// something out of capture: an ignored path or excluded repo directly, an allow list by admitting only what
/// it names. Once the file is gone, a later setup and import capture all of it without asking.
/// </summary>
static class CaptureScopeNotice {
    const int Listed = 10;

    const string KeepAdvice = "    To keep them, run `kcap uninstall --keep-config` instead; it also keeps your profiles and sign-in.";

    /// <summary>The preview lines, or none when the config scopes nothing.</summary>
    public static IReadOnlyList<string> Lines(ConfigRoot config) {
        var path = AppConfig.GetConfigPath(config);

        if (!ConfigMutator.TryLoadPure(path, out var loaded))
            return [
                $"  • Delete {path}, which kcap cannot read: any capture exclusions in it (ignored paths, excluded repos, allow lists) are lost with it",
                KeepAdvice,
            ];

        var scoped = loaded.Profiles
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => (Name: p.Key, Entries: Entries(p.Value).ToList()))
            .Where(p => p.Entries.Count > 0)
            .ToList();
        var entries = scoped
            .SelectMany(p => p.Entries.Select(e => scoped.Count > 1 ? $"{e} (profile {p.Name})" : e))
            .ToList();

        if (entries.Count == 0) return [];

        var lines = new List<string> {
            entries.Count == 1
                ? "  • Delete 1 capture exclusion, so a later setup and import capture what it keeps out:"
                : $"  • Delete {entries.Count} capture exclusions, so a later setup and import capture what they keep out:",
        };
        lines.AddRange(entries.Take(Listed).Select(e => $"      {e}"));
        if (entries.Count > Listed) lines.Add($"      and {entries.Count - Listed} more");
        lines.Add(KeepAdvice);

        return lines;
    }

    static IEnumerable<string> Entries(Profile p) {
        foreach (var e in p.ExcludedPaths ?? []) yield return $"ignored path {e}";
        foreach (var e in p.ExcludedRepos ?? []) yield return $"excluded repo {e}";
        foreach (var e in p.AllowedPaths ?? [])  yield return $"allowed path {e}, outside which nothing is captured";
        foreach (var e in p.AllowedRepos ?? [])  yield return $"allowed repo {e}, outside which nothing is captured";
    }
}
