using System.Text;

namespace Capacitor.Cli.Commands;

/// <summary>Plain-text stand-ins for the marks the CLI prints. An encoder that cannot carry a mark
/// writes "?" for it, or a look-alike such as "." for an ellipsis, and then a wired and an unwired
/// harness read the same and a cut column looks like a full one.</summary>
static class StatusGlyphs {
    static readonly (string Glyph, string Plain)[] Substitutes = [
        ("✓", "ok"),
        ("✗", "x"),
        ("⚠", "!"),
        ("—", "-"),
        ("…", "..."),
        ("→", "->"),
        ("·", "-"),
        ("–", "-"),
    ];

    /// <summary>Whether <paramref name="encoding"/> is a Unicode encoding, the only kind that carries
    /// every mark.</summary>
    internal static bool Encodable(Encoding encoding) =>
        encoding.CodePage is 65001 or 1200 or 1201 or 12000 or 12001;

    /// <summary>Whether standard output carries every mark. The default Windows console does not:
    /// its code page is the OEM one.</summary>
    internal static bool ConsoleEncodable => Encodable(Console.OutputEncoding);

    internal static string Plain(string text) {
        foreach (var (glyph, plain) in Substitutes) text = text.Replace(glyph, plain, StringComparison.Ordinal);

        return text;
    }

    /// <summary><paramref name="text"/> as standard output can carry it.</summary>
    internal static string ForConsole(string text) => ConsoleEncodable ? text : Plain(text);

    /// <summary>The mark that ends a cut cell.</summary>
    internal static string Ellipsis(bool encodable) => encodable ? "…" : "...";

    /// <summary><paramref name="value"/> cut to at most <paramref name="width"/> characters, ending in
    /// <see cref="Ellipsis"/> when it was cut.</summary>
    internal static string Fit(string value, int width, bool encodable) {
        if (value.Length <= width) return value;

        var mark = Ellipsis(encodable);

        return width <= mark.Length ? value[..Math.Max(width, 0)] : value[..(width - mark.Length)] + mark;
    }
}
