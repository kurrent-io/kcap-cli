using System.Text.RegularExpressions;

namespace Capacitor.Cli.Core.WorkItems;

/// <summary>A heuristic, not a classifier: whether an agent's closing message reads as a report that
/// the task is done. A false positive costs one short "not complete yet" turn, once per session; a
/// false negative leaves the SessionStart instruction to do the job.</summary>
public static partial class WrapUpSignals {
    const string Markers = "done|complete|completed|summary|recap|wrapped up|next steps|shipped|merged";

    // A markdown heading counts whatever follows the marker; a plain or bulleted line only when the
    // marker stands alone or is followed by punctuation, so "Complete the migration first" does not.
    [GeneratedRegex(
        @"^[ \t]*(?:#{1,6}[ \t]*(?:\*\*|__)?[ \t]*(?:" + Markers + @")\b" +
        @"|(?:[-*+][ \t]+)?(?:\*\*|__)?[ \t]*(?:" + Markers + @")(?:\*\*|__)?[ \t]*(?:[:.!—–-]|$))",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingMarker();

    [GeneratedRegex(
        @"all tests pass|tests are green|ci is green|opened PR|PR #|ready for review|ready to merge",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Phrase();

    public static bool LooksLikeWrapUp(string? text) {
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim().TrimEnd('*', '_', ' ', '\t', '\r', '\n');
        if (trimmed.EndsWith('?')) return false;

        return LeadingMarker().IsMatch(trimmed) || Phrase().IsMatch(trimmed);
    }
}
