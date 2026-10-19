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
    [Arguments(4, null, "```suggestion\n\n```", "```diff\n-e\n+\n```")]
    [Arguments(4, null, "```suggestion\r\nE\r\n```", "```diff\n-e\n+E\n```")]
    [Arguments(4, null, "```suggestion\nE\n````", "```diff\n-e\n+E\n```")]
    public async Task A_suggestion_reads_as_a_diff_of_the_anchored_lines(int line, int? startLine, string body, string expected) =>
        await Assert.That(SuggestionDiff.Rewrite(body, Hunk, hunkTruncated: false, "right", line, startLine)).IsEqualTo(expected);

    [Test]
    [Arguments(false, "right", 4, 1)]
    [Arguments(true, "right", 4, null)]
    [Arguments(false, "left", 4, null)]
    [Arguments(false, "right", null, null)]
    public async Task Unreadable_anchored_lines_leave_the_comment_as_written(bool truncated, string side, int? line, int? startLine) {
        const string body = "```suggestion\nD\n```";
        // Lines 1..4 ask for four anchored lines from a hunk that shows two on its new side.
        var hunk = startLine == 1 ? "@@ -1,2 +1,2 @@\n a\n-b\n+c" : Hunk;

        await Assert.That(SuggestionDiff.Rewrite(body, hunk, truncated, side, line, startLine)).IsEqualTo(body);
    }

    /// A hunk carries no file headers, so an added line whose text starts with `++` is a line like any other.
    [Test]
    public async Task An_added_line_starting_with_plus_signs_is_anchored() =>
        await Assert.That(SuggestionDiff.Rewrite("```suggestion\nx\n```", "@@ -1 +1,2 @@\n a\n+++b", false, "right", 2, null))
            .IsEqualTo("```diff\n-++b\n+x\n```");

    /// An example quoted inside another code block is code, not a suggestion.
    [Test]
    public async Task A_suggestion_quoted_inside_another_block_is_unchanged() {
        const string body = "````md\n```suggestion\nE\n```\n````";
        await Assert.That(SuggestionDiff.Rewrite(body, Hunk, false, "right", 4, null)).IsEqualTo(body);
    }

    [Test]
    public async Task A_comment_without_a_suggestion_is_unchanged() =>
        await Assert.That(SuggestionDiff.Rewrite("```csharp\nx\n```", Hunk, false, "right", 4, null)).IsEqualTo("```csharp\nx\n```");
}
