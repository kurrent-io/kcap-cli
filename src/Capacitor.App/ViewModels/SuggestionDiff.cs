using System.Text;
using System.Text.RegularExpressions;

namespace Capacitor.App.ViewModels;

/// A review comment's ```` ```suggestion ```` fence, which proposes a replacement for the lines the
/// comment is anchored to, rewritten as a `diff` fence: the anchored lines removed, the fence's
/// body added. The anchored lines are the last ones on the new side of the thread's hunk, which
/// GitHub ends at the commented line.
public static partial class SuggestionDiff {
    [GeneratedRegex(@"^(?<fence>`{3,})suggestion[ \t]*\r?\n(?<body>.*?)^\k<fence>[ \t]*$", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex Fence();

    /// The body with each suggestion fence shown as a diff, or unchanged when the anchored lines
    /// cannot be read: a truncated hunk, a comment on the old side, or a hunk shorter than the range.
    public static string? Rewrite(string? body, string? hunk, bool hunkTruncated, string? side, int? line, int? startLine) {
        if (body is null || hunk is null || hunkTruncated || side is not (null or "RIGHT") || line is not { } last) return body;
        var count = last - (startLine ?? last) + 1;
        var anchored = NewSide(hunk);
        if (count < 1 || anchored.Count < count) return body;
        var removed = anchored.GetRange(anchored.Count - count, count);

        return Fence().Replace(body, match => AsDiff(removed, match.Groups["body"].Value));
    }

    /// The lines a hunk shows on its new side, without their markers.
    static List<string> NewSide(string hunk) =>
        hunk.Replace("\r\n", "\n").Split('\n')
            .Where(l => l.Length > 0 && l[0] is ' ' or '+')
            .Select(l => l[1..])
            .ToList();

    static string AsDiff(List<string> removed, string suggested) {
        var added = suggested.Replace("\r\n", "\n").TrimEnd('\n') is { Length: > 0 } text ? text.Split('\n') : [];
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
