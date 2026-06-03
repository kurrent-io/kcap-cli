using System.Text.RegularExpressions;

namespace Capacitor.Cli.Core.Commands;

/// <summary>
/// Parses a PR reference in either shorthand (<c>owner/repo#123</c>) or
/// github.com URL form. Shared by the <c>kcap review</c> CLI command
/// and the <c>kcap mcp review</c> server's per-tool PR argument.
/// </summary>
public static partial class PrRefParser {
    public static bool TryParse(string input, out string owner, out string repo, out int prNumber) {
        owner    = "";
        repo     = "";
        prNumber = 0;

        if (string.IsNullOrWhiteSpace(input)) return false;

        input = input.Trim();

        var urlMatch = UrlPattern().Match(input);

        if (urlMatch.Success) {
            owner    = urlMatch.Groups[1].Value;
            repo     = urlMatch.Groups[2].Value;
            prNumber = int.Parse(urlMatch.Groups[3].Value);

            return true;
        }

        var shortMatch = ShorthandPattern().Match(input);

        if (shortMatch.Success) {
            owner    = shortMatch.Groups[1].Value;
            repo     = shortMatch.Groups[2].Value;
            prNumber = int.Parse(shortMatch.Groups[3].Value);

            return true;
        }

        return false;
    }

    // Accept any of: trailing `/...`, `?query`, `#fragment` — browser-copied
    // GitHub URLs commonly include these suffixes.
    [GeneratedRegex(@"^https?://github\.com/([^/]+)/([^/]+)/pull/(\d+)(?:[/?#].*)?$")]
    private static partial Regex UrlPattern();

    // Repo group forbids `/` — GitHub repo names can't contain it, and allowing
    // it would let `owner/foo/bar#42` parse into a malformed API URL segment.
    [GeneratedRegex(@"^([^/]+)/([^/#]+)#(\d+)$")]
    private static partial Regex ShorthandPattern();
}
