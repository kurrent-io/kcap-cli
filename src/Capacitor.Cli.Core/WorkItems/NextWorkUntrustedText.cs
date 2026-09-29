using System.Text.RegularExpressions;

namespace Capacitor.Cli.Core.WorkItems;

/// <summary>Every next-work field an agent reads is tracker or model text; this is the one place it
/// is made safe to place inside a delimited data block. Mirrors the server's sanitiser, which has
/// already applied it to the rows it injects — applied again here for a server that has not.</summary>
public static partial class NextWorkUntrustedText {
    [GeneratedRegex(@"\p{Cc}")]
    private static partial Regex Control();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    public static string Render(string? text, int cap) {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (cap <= 0) return string.Empty;

        var s = Whitespace().Replace(Control().Replace(text, " "), " ").Trim().Replace('<', '‹').Replace('>', '›');
        if (s.Length <= cap) return s;

        // Cut one code unit earlier when the boundary would split a surrogate pair.
        var cut = char.IsHighSurrogate(s[cap - 1]) ? cap - 1 : cap;
        return s[..cut].TrimEnd();
    }
}
