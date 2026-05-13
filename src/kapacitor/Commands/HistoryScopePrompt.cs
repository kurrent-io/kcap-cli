using System.Text;

namespace kapacitor.Commands;

/// <summary>
/// Spectre.Console-backed pickers for history import scope, plus the pure
/// helpers that drive their content. Pure helpers are public for unit testing;
/// Spectre wrappers are added in a separate task.
/// </summary>
public static partial class HistoryScopePrompt {
    /// <summary>
    /// Build the option strings for the "specific repository" sub-picker.
    /// The current cwd's repo (if any) is pinned to the top with a "(current)"
    /// marker; the rest are alphabetized and deduplicated against the current.
    /// </summary>
    public static string[] BuildRepoChoices(
        (string Owner, string Name)?              currentRepo,
        IReadOnlyList<(string Owner, string Name)> discoveredRepos) {
        var distinct = discoveredRepos
            .Select(r => $"{r.Owner}/{r.Name}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (currentRepo is { } current) {
            var currentKey = $"{current.Owner}/{current.Name}";
            distinct.RemoveAll(s => s.Equals(currentKey, StringComparison.OrdinalIgnoreCase));
            distinct.Sort(StringComparer.OrdinalIgnoreCase);
            return [$"{currentKey} (current)", .. distinct];
        }

        distinct.Sort(StringComparer.OrdinalIgnoreCase);
        return [.. distinct];
    }

    /// <summary>
    /// Build the confirmation summary block printed before the y/N prompt.
    /// The same text is printed in non-TTY runs (with --yes) so the imported
    /// scope is recorded in CI logs.
    /// </summary>
    public static string FormatSummary(
        ImportScope            scope,
        int                    matchedCount,
        IReadOnlyList<string>  repoSamples,
        string                 visibilityDescription) {
        var scopeLabel = scope switch {
            ImportScope.All      => "everything",
            ImportScope.Org o    => $"org repos only ({o.OrgLogin})",
            ImportScope.Repo r   => $"repository {r.Owner}/{r.Name}",
            _                    => "?"
        };

        const int sampleLimit = 5;
        var samples = repoSamples.Take(sampleLimit).ToArray();
        var more    = repoSamples.Count - samples.Length;
        var repoLine = samples.Length == 0
            ? "(none)"
            : string.Join(", ", samples) + (more > 0 ? $", +{more} more" : "");

        var sb = new StringBuilder();
        sb.AppendLine("About to import:");
        sb.AppendLine($"  scope:   {scopeLabel}");
        sb.AppendLine($"  matched: {matchedCount} session{(matchedCount == 1 ? "" : "s")} across {repoSamples.Count} repo{(repoSamples.Count == 1 ? "" : "s")}");
        sb.AppendLine($"  repos:   {repoLine}");
        sb.Append   ($"  visibility: {visibilityDescription}");
        return sb.ToString();
    }
}
