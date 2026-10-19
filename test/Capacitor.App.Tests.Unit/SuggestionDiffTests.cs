using Capacitor.App.ViewModels;

namespace Capacitor.App.Tests.Unit;

/// A suggestion fence reads as a diff of the lines it replaces, taken from the end of the hunk's new
/// side; when those lines cannot be read, the comment is left exactly as written.
public class SuggestionDiffTests {
    const string Hunk = "@@ -1,4 +1,4 @@\n a\n-b\n+c\n d\n e";

    [Test]
    [Arguments(4, null, "Try:\n```suggestion\nE\n```\nok", "Try:\n```diff\n-e\n+E\n```\nok")]
    [Arguments(4, 3, "```suggestion\nD\nE\n```", "```diff\n-d\n-e\n+D\n+E\n```")]
    [Arguments(4, null, "```suggestion\n```", "```diff\n-e\n```")]
    [Arguments(4, null, "```suggestion\nuse ```x```\n```", "````diff\n-e\n+use ```x```\n````")]
    public async Task A_suggestion_reads_as_a_diff_of_the_anchored_lines(int line, int? startLine, string body, string expected) =>
        await Assert.That(SuggestionDiff.Rewrite(body, Hunk, hunkTruncated: false, "RIGHT", line, startLine)).IsEqualTo(expected);

    [Test]
    [Arguments(false, "RIGHT", 4, 1)]
    [Arguments(true, "RIGHT", 4, null)]
    [Arguments(false, "LEFT", 4, null)]
    [Arguments(false, "RIGHT", null, null)]
    public async Task Unreadable_anchored_lines_leave_the_comment_as_written(bool truncated, string side, int? line, int? startLine) {
        const string body = "```suggestion\nD\n```";
        // Lines 1..4 ask for four anchored lines from a hunk that shows two on its new side.
        var hunk = startLine == 1 ? "@@ -1,2 +1,2 @@\n a\n-b\n+c" : Hunk;

        await Assert.That(SuggestionDiff.Rewrite(body, hunk, truncated, side, line, startLine)).IsEqualTo(body);
    }

    [Test]
    public async Task A_comment_without_a_suggestion_is_unchanged() =>
        await Assert.That(SuggestionDiff.Rewrite("```csharp\nx\n```", Hunk, false, "RIGHT", 4, null)).IsEqualTo("```csharp\nx\n```");
}
