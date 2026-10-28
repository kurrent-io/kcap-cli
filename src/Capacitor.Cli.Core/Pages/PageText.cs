using System.Collections.Frozen;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Core.Pages;

/// <summary>
/// The readable text of a page: what a person sees, without the markup, styles, scripts, inlined
/// images and <c>hidden</c> elements that make up most of a page's bytes, and none of what its scripts
/// draw. Not an HTML parser — a malformed page still comes back as its text, just with less structure.
/// </summary>
public static partial class PageText {
    // A private-use character, so the whitespace collapse leaves it alone; it becomes a tab after.
    const char CellBreak = '\uE000';

    public static string FromHtml(string html) {
        // Scripts go first: a tag inside one is text, and must not open or close a hidden element.
        var visible = WithoutHiddenElements(NonContent().Replace(html, " "));

        // Source line breaks are layout, as they are to a browser; only block elements break a line.
        var text = Spaces().Replace(visible, " ");
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

    static string WithoutHiddenElements(string html) {
        var result = new StringBuilder(html.Length);
        var from   = 0;

        foreach (Match tag in AnyTag().Matches(html)) {
            if (tag.Index < from || tag.Groups["close"].Success || !IsHidden(tag)) continue;

            result.Append(html, from, tag.Index - from).Append(' ');
            from = EndOf(html, tag);
        }

        return result.Append(html, from, html.Length - from).ToString();
    }

    /// <summary>Read from the attribute names, so <c>hidden</c> inside a quoted value — a class name,
    /// a title — hides nothing.</summary>
    static bool IsHidden(Match open) =>
        open.Groups["attr"].Captures.Any(a => a.Value.Equals("hidden", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Where the element <paramref name="open"/> starts really ends, the way a browser closes it: at
    /// its own close tag, at the close of an element it sits in, or — for an element whose end tag is
    /// optional — at the start of one that closes it implicitly, as a new <c>p</c> closes an open one.
    /// An element still open at the end of the page runs to the end.
    /// </summary>
    static int EndOf(string html, Match open) {
        var name = open.Groups["name"].Value.ToLowerInvariant();

        if (open.Groups["self"].Value == "/" || Void.Contains(name)) return open.Index + open.Length;

        var inside = new List<string>();

        foreach (Match tag in AnyTag().Matches(html, open.Index + open.Length)) {
            var tagName = tag.Groups["name"].Value.ToLowerInvariant();

            if (!tag.Groups["close"].Success) {
                if (inside.Count == 0 && ClosedBy.TryGetValue(name, out var closers) && closers.Contains(tagName)) return tag.Index;

                if (tag.Groups["self"].Value != "/" && !Void.Contains(tagName)) inside.Add(tagName);

                continue;
            }

            var opened = inside.LastIndexOf(tagName);

            if (opened >= 0) {
                inside.RemoveRange(opened, inside.Count - opened);

                continue;
            }

            return tagName == name ? tag.Index + tag.Length : tag.Index;
        }

        return html.Length;
    }

    static readonly FrozenSet<string> Void = FrozenSet.ToFrozenSet(
        ["area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr"]);

    static readonly FrozenSet<string> ParagraphClosers = FrozenSet.ToFrozenSet(
        ["address", "article", "aside", "blockquote", "details", "div", "dl", "fieldset", "figcaption", "figure", "footer",
         "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "main", "nav", "ol", "p", "pre", "section", "table", "ul"]);

    static readonly FrozenDictionary<string, FrozenSet<string>> ClosedBy = new Dictionary<string, FrozenSet<string>> {
        ["p"]      = ParagraphClosers,
        ["li"]     = FrozenSet.ToFrozenSet(["li"]),
        ["dt"]     = FrozenSet.ToFrozenSet(["dt", "dd"]),
        ["dd"]     = FrozenSet.ToFrozenSet(["dt", "dd"]),
        ["option"] = FrozenSet.ToFrozenSet(["option", "optgroup"]),
        ["tr"]     = FrozenSet.ToFrozenSet(["tr"]),
        ["td"]     = FrozenSet.ToFrozenSet(["td", "th", "tr"]),
        ["th"]     = FrozenSet.ToFrozenSet(["td", "th", "tr"])
    }.ToFrozenDictionary();

    [GeneratedRegex(@"<(?<close>/)?(?<name>[a-z][a-z0-9-]*)(?:\s+(?<attr>[^\s""'=<>/]+)(?:\s*=\s*(?:""[^""]*""|'[^']*'|[^\s""'=<>`]+))?)*\s*(?<self>/?)>",
                    RegexOptions.IgnoreCase)]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"<(script|style|template|noscript|head)\b[^>]*>.*?</\1\s*>|<!--.*?-->",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex NonContent();

    [GeneratedRegex(@"</t[dh]\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex Cell();

    [GeneratedRegex(@"</?(p|div|br|li|tr|h[1-6]|section|article|header|footer|main|nav|aside|table|thead|tbody|ul|ol|dl|dt|dd|pre|blockquote|hr|figure|figcaption|details|summary)\b[^>]*>",
                    RegexOptions.IgnoreCase)]
    private static partial Regex Block();

    // Looser than AnyTag, so a malformed tag or a doctype is still dropped rather than read as text.
    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"[\s\u00a0]+")]
    private static partial Regex Spaces();
}
