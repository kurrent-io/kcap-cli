using System.Text.RegularExpressions;

namespace Kapacitor.Cli.Core.Commands;

/// <summary>
/// Parses a PR reference in either shorthand (<c>owner/repo#123</c>) or
/// github.com URL form. Shared by the <c>kapacitor review</c> CLI command
/// and the <c>kapacitor mcp review</c> server's per-tool PR argument.
/// </summary>
public static partial class PrRefParser {
    public static bool TryParse(string input, out string owner, out string repo, out int prNumber) {
        owner    = "";
        repo     = "";
        prNumber = 0;

        if (string.IsNullOrWhiteSpace(input)) return false;

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

    [GeneratedRegex(@"^https?://github\.com/([^/]+)/([^/]+)/pull/(\d+)(?:/.*)?$")]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"^([^/]+)/([^#]+)#(\d+)$")]
    private static partial Regex ShorthandPattern();
}
