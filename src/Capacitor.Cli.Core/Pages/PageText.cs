using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Core.Pages;

/// <summary>
/// The readable text of a page: what a person sees, without the markup, styles, scripts and inlined
/// images that make up most of a page's bytes. Not an HTML parser — a malformed page still comes back
/// as its text, just with less structure.
/// </summary>
public static partial class PageText {
    // A private-use character, so the whitespace collapse leaves it alone; it becomes a tab after.
    const char CellBreak = '';

    public static string FromHtml(string html) {
        // Source line breaks are layout, as they are to a browser; only block elements break a line.
        var text = Spaces().Replace(Hidden().Replace(html, " "), " ");
        text = Cell().Replace(text, CellBreak.ToString());
        text = Block().Replace(text, "\n");
        text = Tag().Replace(text, "");
        text = WebUtility.HtmlDecode(text);

        var result = new StringBuilder(text.Length);
        var blank  = true;

        foreach (var raw in text.Split('\n')) {
            var cells = Spaces().Replace(raw, " ").Split(CellBreak).Select(cell => cell.Trim());
            var line  = string.Join('\t', cells).Trim();

            if (line.Length == 0) {
                if (!blank) result.Append('\n');
                blank = true;

                continue;
            }

            result.Append(line).Append('\n');
            blank = false;
        }

        return result.ToString().Trim();
    }

    [GeneratedRegex(@"<(script|style|template|noscript|head)\b[^>]*>.*?</\1\s*>|<!--.*?-->",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Hidden();

    [GeneratedRegex(@"</t[dh]\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex Cell();

    [GeneratedRegex(@"</?(p|div|br|li|tr|h[1-6]|section|article|header|footer|main|nav|aside|table|thead|tbody|ul|ol|dl|dt|dd|pre|blockquote|hr|figure|figcaption|details|summary)\b[^>]*>",
                    RegexOptions.IgnoreCase)]
    private static partial Regex Block();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"[\s ]+")]
    private static partial Regex Spaces();
}
