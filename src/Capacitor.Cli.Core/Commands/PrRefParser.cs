using System.Globalization;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Core.Commands;

/// <summary>
/// Parses a PR reference in either shorthand (<c>owner/repo#123</c>),
/// GitHub PR URL on any host, or GitLab MR URL form. Shared by the <c>kcap review</c>
/// CLI command and the <c>kcap mcp review</c> server's per-tool PR argument.
/// </summary>
public static partial class PrRefParser {
    public static bool TryParse(string input, out string owner, out string repo, out int prNumber) {
        owner = ""; repo = ""; prNumber = 0;
        if (string.IsNullOrWhiteSpace(input)) return false;
        input = input.Trim();

        var match = GitHubUrlPattern().Match(input);
        if (!match.Success) match = GitLabUrlPattern().Match(input);
        if (!match.Success) match = ShorthandPattern().Match(input);
        // A number too large for an int names no PR.
        if (!match.Success || !int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out prNumber))
            return false;

        owner = match.Groups[1].Value;
        repo  = match.Groups[2].Value;
        return true;
    }

    // GitHub-style PR URL on ANY host (github.com or GitHub Enterprise). owner/repo are
    // single-segment; trailing path/query/fragment (browser copies) tolerated.
    [GeneratedRegex(@"^https?://[^/]+/([^/]+)/([^/]+)/pull/(\d+)(?:[/?#].*)?$")]
    private static partial Regex GitHubUrlPattern();

    // GitLab MR URL. Nested groups supported (§6b): owner is the full
    // namespace path before the project, project is the last segment. The literal
    // "/-/merge_requests/" delimiter disambiguates the greedy owner from the project.
    // Same trailing-suffix tolerance so /diffs, /commits, ?query, #note parse.
    [GeneratedRegex(@"^https?://[^/]+/(.+)/([^/]+)/-/merge_requests/(\d+)(?:[/?#].*)?$")]
    private static partial Regex GitLabUrlPattern();

    // Shorthand owner/repo#123 — single-level, unchanged. Repo forbids '/'.
    [GeneratedRegex(@"^([^/]+)/([^/#]+)#(\d+)$")]
    private static partial Regex ShorthandPattern();
}
