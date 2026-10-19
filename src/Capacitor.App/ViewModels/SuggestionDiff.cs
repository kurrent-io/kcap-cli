using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;

namespace Capacitor.App.ViewModels;

/// A review comment's ```` ```suggestion ```` fence, which proposes a replacement for the lines the
/// comment is anchored to, rewritten as a `diff` fence: the anchored lines removed, the fence's
/// lines added. The anchored lines are read from the thread's hunk by their new-side line numbers.
public static partial class SuggestionDiff {
    [GeneratedRegex(@"^@@ -[0-9]+(?:,[0-9]+)? \+(?<start>[0-9]+)(?:,[0-9]+)? @@")]
    private static partial Regex Header();

    /// The body with each top-level suggestion fence shown as a diff, or unchanged when it cannot be
    /// shown faithfully: a truncated body or hunk, a comment on the old side, or a range the hunk does
    /// not hold, or a range starting on the old side. <paramref name="line"/> and <paramref name="startLine"/> are on the hunk's own side.
    public static string? Rewrite(
            string? body, bool bodyTruncated, string? hunk, bool hunkTruncated, string? side, string? startSide, int? line, int? startLine) {
        if (body is null || bodyTruncated || hunk is null || hunkTruncated || line is not { } last) return body;
        if (!OnNewSide(side) || !OnNewSide(startSide)) return body;
        var first = startLine ?? last;
        if (first > last) return body;
        var removed = NewSide(hunk).Where(l => l.Number >= first && l.Number <= last).Select(l => l.Text).ToList();
        if (removed.Count != last - first + 1) return body;

        // Parsed rather than matched, so fences follow CommonMark: any line ending, a longer closing
        // run, and an example quoted inside another block left as written.
        var suggestions = Markdown.Parse(body).OfType<FencedCodeBlock>()
            .Where(block => block.Info == "suggestion")
            .OrderByDescending(block => block.Span.Start)
            .ToList();
        if (suggestions.Count == 0) return body;

        var rewritten = new StringBuilder(body);
        foreach (var block in suggestions) {
            var added = Enumerable.Range(0, block.Lines.Count).Select(i => block.Lines.Lines[i].ToString().TrimEnd('\r'));
            rewritten.Remove(block.Span.Start, block.Span.Length).Insert(block.Span.Start, AsDiff(removed, added));
        }

        return rewritten.ToString();
    }

    static bool OnNewSide(string? side) => side is null || side.Equals("right", StringComparison.OrdinalIgnoreCase);

    /// The lines a hunk shows on its new side, numbered from its header, without their markers. A hunk
    /// has no file headers, so a line starting `+++` is an added line like any other.
    static IEnumerable<(int Number, string Text)> NewSide(string hunk) {
        var lines = hunk.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || Header().Match(lines[0]) is not { Success: true } header
            || !int.TryParse(header.Groups["start"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) yield break;
        foreach (var l in lines.Skip(1)) {
            if (l.Length == 0 || l[0] is not (' ' or '+')) continue;
            yield return (number++, l[1..]);
        }
    }

    static string AsDiff(List<string> removed, IEnumerable<string> added) {
        var lines = removed.Select(l => "-" + l).Concat(added.Select(l => "+" + l)).ToList();
        // Longer than any backtick run inside, so a suggestion that itself holds a fence stays inside this one.
        var fence = new string('`', Math.Max(3, LongestRun(lines) + 1));
        var diff = new StringBuilder().Append(fence).Append("diff\n");
        foreach (var l in lines) diff.Append(l).Append('\n');

        return diff.Append(fence).ToString();
    }

    static int LongestRun(IEnumerable<string> lines) {
        var longest = 0;
        foreach (var l in lines) {
            var run = 0;
            foreach (var c in l) {
                run = c == '`' ? run + 1 : 0;
                longest = Math.Max(longest, run);
            }
        }

        return longest;
    }
}
