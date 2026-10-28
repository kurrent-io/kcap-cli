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

    readonly record struct Element(string Name, bool Suppressed, bool Foreign);

    static bool Suppressed(List<Element> open) => open.Count > 0 && open[^1].Suppressed;

    /// <summary>Consumes the markup at <paramref name="lt"/> and returns where text resumes. A
    /// <c>&lt;</c> that starts no tag is text, as it is to a browser.</summary>
    static int ReadMarkup(string html, int lt, List<Element> open, StringBuilder text) {
        if (string.CompareOrdinal(html, lt, "<!--", 0, 4) == 0) return After(html, "-->", lt + 4);

        if (lt + 1 < html.Length && html[lt + 1] is '!' or '?') return After(html, ">", lt + 2);

        if (ReadTag(html, lt) is not { } tag) {
            // "</" without a letter is a bogus comment to a browser, up to the next ">".
            if (lt + 1 < html.Length && html[lt + 1] == '/') return After(html, ">", lt + 2);

            if (!Suppressed(open)) text.Append('<');

            return lt + 1;
        }

        var (name, end) = (tag.Name, tag.End);

        if (tag.IsClose) {
            Close(name, open, text);

            return end;
        }

        while (open.Count > 0 && ClosesImplicitly(open[^1].Name, name)) Pop(open, text);

        // Script and style bodies are raw text: a tag inside one is not markup.
        if (RawText.Contains(name)) return RawTextEnd(html, $"</{name}", end);

        var suppressed = Suppressed(open) || NonContent.Contains(name) || tag.IsHidden;

        if (!suppressed && Block.Contains(name)) text.Append('\n');

        // A trailing "/" closes only SVG and MathML elements; in HTML only a void element has no content.
        var foreign = name is "svg" or "math" || open.Count > 0 && open[^1].Foreign;

        if (!Void.Contains(name) && !(foreign && tag.IsSelfClosing)) open.Add(new(name, suppressed, foreign));

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

    readonly record struct Tag(string Name, bool IsClose, bool IsSelfClosing, bool IsHidden, int End);

    /// <summary>
    /// The tag at <paramref name="lt"/> read as a browser's tokenizer reads it, or null when the
    /// <c>&lt;</c> starts none — only a letter, or <c>/</c> and a letter, starts one. Attributes are
    /// split on whitespace or <c>/</c>, a quoted value is consumed whole whatever follows it, and
    /// <c>hidden</c> counts only as an attribute name, never inside a value. A tag the page ends inside
    /// runs to the end, as the browser drops it.
    /// </summary>
    static Tag? ReadTag(string html, int lt) {
        var at      = lt + 1;
        var isClose = at < html.Length && html[at] == '/';

        if (isClose) at++;

        if (at >= html.Length || !char.IsAsciiLetter(html[at])) return null;

        var name = ReadName(html, ref at, "\t\n\f\r />").ToLowerInvariant();

        var (hidden, selfClosing) = (false, false);

        while (at < html.Length) {
            var c = html[at];

            if (c == '>') return new(name, isClose, selfClosing, hidden, at + 1);

            if (char.IsWhiteSpace(c) || c == '/') {
                selfClosing = c == '/';
                at++;

                continue;
            }

            selfClosing = false;

            // A name is at least one character, so a stray "=" starts one rather than looping here.
            var attribute = html[at] + ReadName(html, ref at, "\t\n\f\r />=", skipFirst: true);

            hidden |= attribute.Equals("hidden", StringComparison.OrdinalIgnoreCase);

            while (at < html.Length && char.IsWhiteSpace(html[at])) at++;

            if (at >= html.Length || html[at] != '=') continue;

            at++;

            while (at < html.Length && char.IsWhiteSpace(html[at])) at++;

            if (at < html.Length && html[at] is '"' or '\'') {
                var close = html.IndexOf(html[at], at + 1);

                at = close < 0 ? html.Length : close + 1;
            } else {
                ReadName(html, ref at, "\t\n\f\r >");
            }
        }

        return new(name, isClose, false, hidden, html.Length);
    }

    static string ReadName(string html, ref int at, string stops, bool skipFirst = false) {
        var from = at;

        if (skipFirst) at++;

        while (at < html.Length && !stops.Contains(html[at])) at++;

        return html[(skipFirst ? from + 1 : from)..at];
    }

    static int After(string html, string marker, int from) {
        var at = html.IndexOf(marker, from, StringComparison.Ordinal);

        return at < 0 ? html.Length : at + marker.Length;
    }

    /// <summary>Where the raw text's own close tag starts: <paramref name="close"/> followed by a tag
    /// boundary, so <c>&lt;/scripts&gt;</c> inside a script does not end it.</summary>
    static int RawTextEnd(string html, string close, int from) {
        for (var at = html.IndexOf(close, from, StringComparison.OrdinalIgnoreCase);
             at >= 0;
             at = html.IndexOf(close, at + 1, StringComparison.OrdinalIgnoreCase)) {
            var next = at + close.Length;

            if (next == html.Length || html[next] is '>' or '/' || char.IsWhiteSpace(html[next])) return at;
        }

        return html.Length;
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

    [GeneratedRegex(@"[\s ]+")]
    private static partial Regex Spaces();
}
