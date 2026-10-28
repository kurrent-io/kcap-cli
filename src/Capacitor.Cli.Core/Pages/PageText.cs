using System.Collections.Frozen;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Core.Pages;

/// <summary>
/// The readable text of a page: what a person sees, without the markup, styles, scripts, inlined
/// images and <c>hidden</c> elements that make up most of a page's bytes, and none of what its scripts
/// draw.
///
/// <para>One pass over the markup with a stack of open elements, closed the way a browser closes
/// them: a close tag with no open element of its name is ignored, and an element whose end tag is
/// optional ends where the next one that closes it starts. Anything inside a hidden or non-content
/// element stays out, however it nests.</para>
/// </summary>
public static partial class PageText {
    // A private-use character, so the whitespace collapse leaves it alone; it becomes a tab after.
    const char CellBreak = '';

    public static string FromHtml(string html) {
        var text = new StringBuilder(html.Length);
        var open = new List<Element>();
        var at   = 0;

        while (at < html.Length) {
            var lt = html.IndexOf('<', at);

            if (lt < 0) lt = html.Length;

            // Source line breaks are layout, as they are to a browser; only block elements break a line.
            if (!Suppressed(open)) text.Append(Spaces().Replace(html[at..lt], " "));

            if (lt == html.Length) break;

            at = ReadMarkup(html, lt, open, text);
        }

        return Lines(WebUtility.HtmlDecode(text.ToString()));
    }

    readonly record struct Element(string Name, bool Suppressed);

    static bool Suppressed(List<Element> open) => open.Count > 0 && open[^1].Suppressed;

    /// <summary>Consumes the markup at <paramref name="lt"/> and returns where text resumes. A
    /// <c>&lt;</c> that starts no tag is text, as it is to a browser.</summary>
    static int ReadMarkup(string html, int lt, List<Element> open, StringBuilder text) {
        if (string.CompareOrdinal(html, lt, "<!--", 0, 4) == 0) return After(html, "-->", lt + 4);

        if (lt + 1 < html.Length && html[lt + 1] is '!' or '?') return After(html, ">", lt + 2);

        var tag = OpenOrClose().Match(html, lt);

        if (!tag.Success) {
            if (!Suppressed(open)) text.Append('<');

            return lt + 1;
        }

        var name = tag.Groups["name"].Value.ToLowerInvariant();
        var end  = lt + tag.Length;

        if (tag.Groups["close"].Success) {
            Close(name, open, text);

            return end;
        }

        while (open.Count > 0 && ClosesImplicitly(open[^1].Name, name)) Pop(open, text);

        // Script and style bodies are raw text: a tag inside one is not markup.
        if (RawText.Contains(name)) return Before(html, $"</{name}", end);

        var suppressed = Suppressed(open) || NonContent.Contains(name) || IsHidden(tag);

        if (!suppressed && Block.Contains(name)) text.Append('\n');

        if (tag.Groups["self"].Value != "/" && !Void.Contains(name)) open.Add(new(name, suppressed));

        return end;
    }

    static void Close(string name, List<Element> open, StringBuilder text) {
        var at = open.FindLastIndex(e => e.Name == name);

        if (at < 0) return;

        while (open.Count > at) Pop(open, text);
    }

    static void Pop(List<Element> open, StringBuilder text) {
        var element = open[^1];
        open.RemoveAt(open.Count - 1);

        if (element.Suppressed) return;

        if (element.Name is "td" or "th") text.Append(CellBreak);
        else if (Block.Contains(element.Name)) text.Append('\n');
    }

    /// <summary>Read from the attribute names, so <c>hidden</c> inside a quoted value — a class name,
    /// a title — hides nothing.</summary>
    static bool IsHidden(Match open) =>
        open.Groups["attr"].Captures.Any(a => a.Value.Equals("hidden", StringComparison.OrdinalIgnoreCase));

    static int After(string html, string marker, int from) {
        var at = html.IndexOf(marker, from, StringComparison.Ordinal);

        return at < 0 ? html.Length : at + marker.Length;
    }

    static int Before(string html, string marker, int from) {
        var at = html.IndexOf(marker, from, StringComparison.OrdinalIgnoreCase);

        return at < 0 ? html.Length : at;
    }

    static string Lines(string text) {
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

    static bool ClosesImplicitly(string open, string next) =>
        ClosedBy.TryGetValue(open, out var closers) && closers.Contains(next);

    static readonly FrozenSet<string> RawText = FrozenSet.ToFrozenSet(["script", "style"]);

    static readonly FrozenSet<string> NonContent = FrozenSet.ToFrozenSet(["head", "template", "noscript"]);

    static readonly FrozenSet<string> Void = FrozenSet.ToFrozenSet(
        ["area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "source", "track", "wbr"]);

    static readonly FrozenSet<string> Block = FrozenSet.ToFrozenSet(
        ["address", "article", "aside", "blockquote", "br", "details", "div", "dl", "dd", "dt", "fieldset", "figcaption",
         "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "li", "main", "nav", "ol", "p",
         "pre", "section", "summary", "table", "tbody", "tfoot", "thead", "tr", "ul"]);

    static readonly FrozenSet<string> ParagraphClosers = FrozenSet.ToFrozenSet(
        ["address", "article", "aside", "blockquote", "details", "div", "dl", "fieldset", "figcaption", "figure", "footer",
         "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "main", "nav", "ol", "p", "pre", "section", "table", "ul"]);

    static readonly FrozenSet<string> TableSections = FrozenSet.ToFrozenSet(["thead", "tbody", "tfoot"]);

    static readonly FrozenDictionary<string, FrozenSet<string>> ClosedBy = new Dictionary<string, FrozenSet<string>> {
        ["p"]      = ParagraphClosers,
        ["li"]     = FrozenSet.ToFrozenSet(["li"]),
        ["dt"]     = FrozenSet.ToFrozenSet(["dt", "dd"]),
        ["dd"]     = FrozenSet.ToFrozenSet(["dt", "dd"]),
        ["option"] = FrozenSet.ToFrozenSet(["option", "optgroup"]),
        ["thead"]  = TableSections,
        ["tbody"]  = TableSections,
        ["tfoot"]  = TableSections,
        ["tr"]     = FrozenSet.ToFrozenSet(["tr", .. TableSections]),
        ["td"]     = FrozenSet.ToFrozenSet(["td", "th", "tr", .. TableSections]),
        ["th"]     = FrozenSet.ToFrozenSet(["td", "th", "tr", .. TableSections])
    }.ToFrozenDictionary();

    // Anchored where the scan stands. Attribute values, quoted or not, are consumed whole, so a ">"
    // inside one does not end the tag.
    [GeneratedRegex(@"\G<(?<close>/)?(?<name>[a-z][a-z0-9-]*)(?:\s+(?<attr>[^\s""'=<>/]+)(?:\s*=\s*(?:""[^""]*""|'[^']*'|[^\s""'=<>`]+))?)*\s*(?<self>/?)>",
                    RegexOptions.IgnoreCase)]
    private static partial Regex OpenOrClose();

    [GeneratedRegex(@"[\s ]+")]
    private static partial Regex Spaces();
}
