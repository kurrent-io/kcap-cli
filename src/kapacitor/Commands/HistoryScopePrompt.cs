using System.Text;
using Spectre.Console;

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

public static partial class HistoryScopePrompt {
    /// <summary>
    /// Run the top-level scope picker. Returns the resolved scope, or null
    /// when the user picks "specific repository" but the sub-picker has no
    /// options (no current repo + no detected repos).
    /// </summary>
    public static ImportScope? RunPicker(
        string                                     activeProfile,
        (string Owner, string Name)?               currentRepo,
        IReadOnlyList<(string Owner, string Name)> discoveredRepos) {
        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("What would you like to import?")
                .AddChoices("all", "org", "repo")
                .UseConverter(c => c switch {
                    "all"  => "Everything",
                    "org"  => $"Org repos only ({activeProfile})",
                    "repo" => "Specific repository",
                    _      => c,
                }));

        if (choice == "all")  return new ImportScope.All();
        if (choice == "org") {
            if (string.IsNullOrEmpty(activeProfile) || activeProfile == "default") {
                AnsiConsole.MarkupLine("[red]Active profile has no org. Run `kapacitor setup`.[/]");
                return null;
            }
            return new ImportScope.Org(activeProfile);
        }

        var repoChoices = BuildRepoChoices(currentRepo, discoveredRepos);
        if (repoChoices.Length == 0) {
            AnsiConsole.MarkupLine("[red]No repositories detected in discovered sessions.[/]");
            return null;
        }

        var picked = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Which repository?")
                .PageSize(15)
                .AddChoices(repoChoices));

        // Strip the trailing " (current)" marker before splitting on '/'.
        var clean = picked.EndsWith(" (current)") ? picked[..^" (current)".Length] : picked;
        var parts = clean.Split('/');
        return new ImportScope.Repo(parts[0], parts[1]);
    }

    /// <summary>
    /// Print the summary block to stderr (visible even when stdout is
    /// redirected) and prompt y/N if <paramref name="skip"/> is false.
    /// Returns true to proceed with the import.
    /// </summary>
    public static bool PromptConfirm(
        ImportScope            scope,
        int                    matchedCount,
        IReadOnlyList<string>  repoSamples,
        string                 visibilityDescription,
        bool                   skip) {
        var summary = FormatSummary(scope, matchedCount, repoSamples, visibilityDescription);
        Console.Error.WriteLine(summary);

        if (skip) return true;

        return AnsiConsole.Prompt(
            new ConfirmationPrompt("Continue?") { DefaultValue = false });
    }
}
