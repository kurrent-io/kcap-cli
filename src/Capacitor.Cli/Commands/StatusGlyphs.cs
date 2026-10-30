using System.Text;

namespace Capacitor.Cli.Commands;

/// <summary>Plain-text stand-ins for the marks <c>kcap status</c> prints. An encoder that cannot carry
/// a mark writes "?" for it, and then a wired and an unwired harness read the same.</summary>
static class StatusGlyphs {
    static readonly (string Glyph, string Plain)[] Substitutes = [
        ("✓", "ok"),
        ("✗", "x"),
        ("⚠", "!"),
        ("—", "-"),
        ("…", "..."),
    ];

    /// <summary>Whether <paramref name="encoding"/> is a Unicode encoding, the only kind that carries
    /// every mark.</summary>
    internal static bool Encodable(Encoding encoding) =>
        encoding.CodePage is 65001 or 1200 or 1201 or 12000 or 12001;

    internal static string Plain(string text) {
        foreach (var (glyph, plain) in Substitutes) text = text.Replace(glyph, plain, StringComparison.Ordinal);

        return text;
    }
}
