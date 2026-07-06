using System.Text;

namespace Capacitor.Cli.Core.Curation;

public static class CuratedTextNormalizer {
    /// <summary>
    /// Promoted text is untrusted/multiline and only length-validated server-side.
    /// Collapse it to a single logical line and neutralize HTML-comment terminators
    /// so it can never be mistaken for a managed-block marker.
    /// </summary>
    public static string? Normalize(string? raw) {
        if (string.IsNullOrEmpty(raw)) return null;

        var sb        = new StringBuilder(raw.Length);
        var lastSpace = false;
        foreach (var ch in raw) {
            var c = ch is '\r' or '\n' or '\t' ? ' ' : ch;
            if (c == ' ') {
                if (!lastSpace) sb.Append(' ');
                lastSpace = true;
            } else {
                sb.Append(c);
                lastSpace = false;
            }
        }

        var s = sb.ToString().Trim();
        return s.Length == 0 ? null :
            // Defang the comment terminator: a zero-width space (U+200B) breaks the "-->"
            // token, which also neutralizes any embedded start/end marker (both end in "-->").
            // Use the \u200b escape, never a literal ZWSP, so the source stays reviewable.
            s.Replace("-->", "--\u200b>");
    }
}
