using System.Buffers;

namespace Capacitor.App.Services;

/// Swaps the symbols an agent's status line draws that the pane's monospace stack carries no
/// glyph for. The terminal draws each styled run as one string from its first cell, so a fallback
/// glyph wider than a cell pushes the rest of its run right and over the next run, and lays the
/// run out on the fallback typeface's baseline. Each replacement is in Menlo, Consolas and DejaVu
/// Sans Mono alike and occupies one cell like the character it stands in for.
public static class TerminalGlyphSubstitution {
    // Consolas has ► (U+25BA) but not ▶ (U+25B6), so the right-pointing marker must be the pointer.
    static readonly (char From, char To)[] Map = [
        ('⏵', '►'), // Claude Code's permission-mode marker
        ('⏺', '●'), // tool-call and message bullet
        ('⎿', '└'), // tool-result elbow
        ('❯', '›'), // prompt chevron
        ('✢', '+'), // spinner frames
        ('✳', '*'),
        ('✻', '*'),
        ('✽', '*'),
    ];

    static readonly SearchValues<char> Targets = SearchValues.Create(Map.Select(m => m.From).ToArray());

    public static string Apply(string text) => Apply(text, 0, text.Length);

    /// The <paramref name="length"/> characters from <paramref name="start"/>, rewritten; the
    /// text itself when that is all of it and nothing needs swapping.
    public static string Apply(string text, int start, int length) {
        var slice = text.AsSpan(start, length);
        var first = slice.IndexOfAny(Targets);
        if (first < 0) return length == text.Length ? text : slice.ToString();

        return string.Create(length, (text, start, first), static (dest, state) => {
            state.text.AsSpan(state.start, dest.Length).CopyTo(dest);
            for (var i = state.first; i < dest.Length; i++) {
                foreach (var (from, to) in Map) {
                    if (dest[i] != from) continue;
                    dest[i] = to;
                    break;
                }
            }
        });
    }
}
