using System.Text;
using Spectre.Console;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Spectre.Console-backed pickers for import scope, plus the pure
/// helpers that drive their content. Pure helpers are public for unit testing;
/// Spectre wrappers are added in a separate task.
/// </summary>
public static partial class ImportScopePrompt {
    /// <summary>
    /// Build the option strings for the "specific repository" sub-picker.
    /// The current cwd's repo (if any) is pinned to the top with a "(current)"
    /// marker; the rest are alphabetized and deduplicated against the current.
    /// </summary>
    public static string[] BuildRepoChoices(
            (string Owner, string Name)?               currentRepo,
            IReadOnlyList<(string Owner, string Name)> discoveredRepos
        ) {
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
            ImportScope           scope,
            int                   matchedCount,
            IReadOnlyList<string> repoSamples,
            string                visibilityDescription
        ) {
        var sb = new StringBuilder();
        sb.AppendLine("About to import:");
        sb.AppendLine($"  scope:   {ScopeLabel(scope)}");
        sb.AppendLine($"{MatchedPrefix}{matchedCount}{MatchedSuffix(matchedCount, repoSamples.Count)}");
        sb.AppendLine($"  repos:   {RepoLine(repoSamples)}");
        sb.Append($"  visibility: {visibilityDescription}");

        return sb.ToString();
    }

    // Shared between the plain-text FormatSummary and the colored stderr
    // renderer in PromptConfirm so both code paths stay byte-for-byte
    // identical to each other without either one parsing the other's
    // output.
    const string MatchedPrefix = "  matched: ";

    static string ScopeLabel(ImportScope scope) => scope switch {
        ImportScope.All    => "everything",
        ImportScope.Org o  => $"org repos only ({o.OrgLogin})",
        ImportScope.Repo r => $"repository {r.Owner}/{r.Name}",
        _                  => "?"
    };

    static string MatchedSuffix(int matchedCount, int repoCount) =>
        $" session{(matchedCount == 1 ? "" : "s")} across {repoCount} repo{(repoCount == 1 ? "" : "s")}";

    static string RepoLine(IReadOnlyList<string> repoSamples) {
        const int sampleLimit = 5;
        var       samples     = repoSamples.Take(sampleLimit).ToArray();
        var       more        = repoSamples.Count - samples.Length;

        return samples.Length == 0
            ? "(none)"
            : string.Join(", ", samples) + (more > 0 ? $", +{more} more" : "");
    }
}

public static partial class ImportScopePrompt {
    /// <summary>
    /// Run the top-level scope picker. Returns the resolved scope, or null
    /// when the user picks "specific repository" but the sub-picker has no
    /// options (no current repo + no detected repos).
    /// </summary>
    public static ImportScope? RunPicker(
            string                                     activeProfile,
            (string Owner, string Name)?               currentRepo,
            IReadOnlyList<(string Owner, string Name)> discoveredRepos
        ) {
        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("What would you like to import?")
                .AddChoices("all", "org", "repo")
                .UseConverter(c => c switch {
                        "all"  => "Everything",
                        "org"  => $"Org repos only ({activeProfile})",
                        "repo" => "Specific repository",
                        _      => c,
                    }
                )
        );

        switch (choice) {
            case "all":
                return new ImportScope.All();
            case "org" when string.IsNullOrEmpty(activeProfile) || activeProfile == "default":
                AnsiConsole.MarkupLine("[red]Active profile has no org. Run `kcap setup`.[/]");

                return null;
            case "org":
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
                .AddChoices(repoChoices)
        );

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
            ImportScope           scope,
            int                   matchedCount,
            IReadOnlyList<string> repoSamples,
            string                visibilityDescription,
            bool                  skip
        ) {
        WriteSummaryToStderr(scope, matchedCount, repoSamples, visibilityDescription);

        if (skip) return true;

        return AnsiConsole.Prompt(
            new ConfirmationPrompt("Continue?") { DefaultValue = false }
        );
    }

    // Render the summary to stderr (visible even when stdout is redirected),
    // highlighting the matched-session count so it's the line the operator's
    // eye lands on. Lines are composed from the same per-component helpers
    // FormatSummary uses, so the rendered text stays in lock-step with the
    // canonical form without parsing it. Spectre auto-strips markup on
    // non-TTY stderr (e.g. CI), so the printed text matches FormatSummary
    // byte-for-byte in CI logs.
    static void WriteSummaryToStderr(
            ImportScope           scope,
            int                   matchedCount,
            IReadOnlyList<string> repoSamples,
            string                visibilityDescription
        ) {
        var console = AnsiConsole.Create(
            new AnsiConsoleSettings { Out = new AnsiConsoleOutput(Console.Error) }
        );

        console.WriteLine("About to import:");
        console.WriteLine($"  scope:   {ScopeLabel(scope)}");
        console.MarkupLine(
            $"{MatchedPrefix}[bold cyan]{matchedCount}[/]{Markup.Escape(MatchedSuffix(matchedCount, repoSamples.Count))}"
        );
        console.WriteLine($"  repos:   {RepoLine(repoSamples)}");
        console.WriteLine($"  visibility: {visibilityDescription}");
    }
}
