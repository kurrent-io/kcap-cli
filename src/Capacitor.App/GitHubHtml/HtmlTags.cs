namespace Capacitor.App.GitHubHtml;

/// The HTML the reader converts. Names are lower-case, as the tokenizer delivers them.
public static class HtmlTags {
    public static HtmlTagClass Classify(string name) => name switch {
        "img" or "br"          => HtmlTagClass.Void,
        "details"              => HtmlTagClass.Structural,
        "summary" or "pre"     => HtmlTagClass.Paired,
        _ when IsInline(name)  => HtmlTagClass.Paired,
        _                      => HtmlTagClass.Unknown,
    };

    /// Paired tags a paragraph may hold; `summary` and `pre` pair too, but only at block level.
    public static bool IsInline(string name) => name == "a" || IsCode(name) || TryEmphasis(name, out _, out _);

    public static bool IsCode(string name) => name is "code" or "kbd" or "tt" or "samp";

    public static bool TryEmphasis(string name, out char delimiter, out int count) {
        (delimiter, count) = name switch {
            "b" or "strong"            => ('*', 2),
            "i" or "em"                => ('*', 1),
            "del" or "s" or "strike"   => ('~', 2),
            "ins"                      => ('+', 2),
            "sub"                      => ('~', 1),
            "sup"                      => ('^', 1),
            _                          => ('\0', 0),
        };
        return count > 0;
    }
}
