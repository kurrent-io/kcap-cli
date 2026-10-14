using System.Text;
using Capacitor.Cli.Core.NextWork;

namespace Capacitor.App.ViewModels;

public static class NextWorkPromptBuilder {
    public static string BuildForRow(NextWorkFeedRowDto row) {
        if (!string.IsNullOrWhiteSpace(row.LaunchPrompt))
            return row.LaunchPrompt.Trim();

        var sb = new StringBuilder();
        var label = string.IsNullOrWhiteSpace(row.TargetLabel) ? row.TargetId : row.TargetLabel.Trim();

        switch (row.ResolvedGroup) {
            case "waiting":
                if (string.Equals(row.Arm, "ReviewRequested", StringComparison.OrdinalIgnoreCase))
                    sb.Append($"Review {label}.");
                else if (string.Equals(row.Arm, "ApprovedUnmerged", StringComparison.OrdinalIgnoreCase))
                    sb.Append($"Check {label} and get it ready to merge.");
                else
                    sb.Append($"Follow up on {label}.");
                break;
            case "finish":
                sb.Append($"Continue work on {label}.");
                break;
            default:
                sb.Append($"Work on {label}.");
                break;
        }

        if (!string.IsNullOrWhiteSpace(row.Because)) {
            sb.Append(' ');
            sb.Append(row.Because.Trim());
        }

        var summaries = row.Evidence
            .Select(e => e.Summary.Trim())
            .Where(s => s.Length > 0 && !string.Equals(s, row.Because.Trim(), StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();

        if (summaries.Count > 0) {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append("Context: ");
            sb.Append(string.Join(" · ", summaries));
        }

        return sb.ToString().Trim();
    }

    public static string FrameWithBlueprint(string? goal, StartFromBlueprintKind? blueprint) {
        var trimmed = goal?.Trim() ?? "";
        if (blueprint is null) return trimmed;

        return blueprint.Value switch {
            StartFromBlueprintKind.ReviewPr => trimmed.Length == 0
                ? "Review the pull request and walk through any findings. Close out once signed off."
                : trimmed.StartsWith("Review ", StringComparison.OrdinalIgnoreCase)
                    ? trimmed
                    : $"Review pull request: {trimmed}\n\nAct as reviewer and driver, and close on sign-off.",
            StartFromBlueprintKind.FixCi => trimmed.Length == 0
                ? "Inspect the failing CI job logs first, identify the root cause, and fix the failure."
                : trimmed.Contains("job log", StringComparison.OrdinalIgnoreCase)
                    ? trimmed
                    : $"Fix failing CI: {trimmed}\n\nRead the failing job log first before making changes.",
            StartFromBlueprintKind.ExploreIdea => trimmed.Length == 0
                ? "Explore this repository and help shape an idea. Do not create a branch unless asked."
                : trimmed,
            _ => trimmed,
        };
    }
}
