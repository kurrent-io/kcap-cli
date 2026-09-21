namespace Capacitor.App.GitHubHtml;

/// The HTML the reader converts. Names are lower-case, as the tokenizer delivers them.
public static class HtmlTags {
    public static HtmlTagClass Classify(string name) => name switch {
        _ when IsVoid(name)       => HtmlTagClass.Void,
        _ when IsStructural(name) => HtmlTagClass.Structural,
        "summary" or "pre"        => HtmlTagClass.Paired,
        _ when IsHeading(name, out _) => HtmlTagClass.Paired,
        _ when IsInline(name)     => HtmlTagClass.Paired,
        _                         => HtmlTagClass.Unknown,
    };

    /// `hr` is a block on its own; `source` and `wbr` render nothing.
    public static bool IsVoid(string name) => name is "img" or "br" or "hr" or "source" or "wbr";

    /// Tags whose element is a block container. They pair across the HTML blocks of one parent,
    /// so markdown written between `<div>` and `</div>` lands inside the element.
    public static bool IsStructural(string name) => name is
        "details" or "blockquote" or "ul" or "ol" or "li" or "dl" or "dt" or "dd" or "div" or "p" or
        "table" or "thead" or "tbody" or "tfoot" or "tr" or "td" or "th" or
        "section" or "article" or "center" or "figure" or "figcaption" or "main" or "header" or "footer" or "nav" or "aside";

    public static bool IsHeading(string name, out int level) {
        level = name.Length == 2 && name[0] == 'h' && name[1] is >= '1' and <= '6' ? name[1] - '0' : 0;
        return level > 0;
    }

    /// Paired tags a paragraph may hold; `summary`, `pre` and headings pair too, but only at block level.
    public static bool IsInline(string name) => name == "a" || IsCode(name) || IsTransparent(name) || TryEmphasis(name, out _, out _);

    public static bool IsCode(string name) => name is "code" or "kbd" or "tt" or "samp";

    /// Inline tags that contribute their content and nothing else.
    public static bool IsTransparent(string name) => name is
        "span" or "font" or "small" or "big" or "abbr" or "time" or "label" or "picture" or "relative-time" or "g-emoji" or "bdi" or "bdo";

    public static bool TryEmphasis(string name, out char delimiter, out int count) {
        (delimiter, count) = name switch {
            "b" or "strong"                     => ('*', 2),
            "i" or "em" or "cite" or "dfn" or "var" or "q" => ('*', 1),
            "del" or "s" or "strike"            => ('~', 2),
            "ins" or "u"                        => ('+', 2),
            "mark"                              => ('=', 2),
            "sub"                               => ('~', 1),
            "sup"                               => ('^', 1),
            _                                   => ('\0', 0),
        };
        return count > 0;
    }
}
