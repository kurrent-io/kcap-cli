using System.Text.RegularExpressions;

namespace Capacitor.Cli.Commands;

/// <summary>Cutting and locating text inside judge tool results.</summary>
static class JudgeText {
    static readonly string[] Operators = ["or", "and", "not", "near"];

    /// <summary>A cut position moved back off the middle of a surrogate pair, so neither side holds half a character.</summary>
    public static int Boundary(string text, int at) =>
        at > 0 && at < text.Length && char.IsLowSurrogate(text[at]) && char.IsHighSurrogate(text[at - 1]) ? at - 1 : at;

    /// <summary>Where a search query matched: the query verbatim (the server's substring lane), else its earliest quoted
    /// phrase, else the earliest of its words, split on punctuation as the server's tokenizer does, with negated words and
    /// operators skipped. Whole words are preferred to substrings throughout. 0 when nothing is found.</summary>
    public static int MatchAt(string text, string query) {
        string[] phrase = [query.Trim()];
        var      quoted = Regex.Matches(query, "\"([^\"]+)\"").Select(m => m.Groups[1].Value.Trim()).ToArray();
        var      words  = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !t.StartsWith('-'))
            .SelectMany(t => Regex.Split(t, @"[^\p{L}\p{N}]+"))
            .Where(w => w.Length > 0 && !Operators.Contains(w, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[][] tiers = [phrase, quoted, words];

        foreach (var tier in tiers)
            if (tier.Select(t => WordAt(text, t)).Min() is { } whole) return whole;

        foreach (var tier in tiers)
            if (tier.Select(t => SubstringAt(text, t)).Min() is { } part) return part;

        return 0;
    }

    static int? SubstringAt(string text, string term) =>
        term.Length > 0 && text.IndexOf(term, StringComparison.OrdinalIgnoreCase) is var at and >= 0 ? at : null;

    static int? WordAt(string text, string word) {
        if (word.Length == 0) return null;
        var m = Regex.Match(text, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(word)}(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return m.Success ? m.Index : null;
    }
}
