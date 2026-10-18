using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class JudgeTextTests {
    [Test]
    [Arguments("xx needles xx needle", "needle", 14, "a whole word before an earlier substring")]
    [Arguments("xx other xx needle", "-other needle", 12, "a negated word never anchors")]
    [Arguments("xx bar xx foo", "foo/bar", 3, "punctuation splits a query into words")]
    [Arguments("xx and xx needle", "x AND needle", 10, "an operator is not a word")]
    [Arguments("xx Foo/Bar", "foo/bar", 3, "the query verbatim first")]
    [Arguments("red xx red fox", "\"red fox\" -cat", 7, "a quoted phrase before its words")]
    [Arguments("needle xx needle in", "\"needle in\" decoy", 10, "a quoted phrase before an earlier whole word of it")]
    [Arguments("red fox xx needle", "-\"red fox\" needle", 11, "a negated phrase never anchors, nor do its words")]
    [Arguments("red xx red. fox", "\"red fox\"", 7, "a quoted phrase across punctuation the index ignores")]
    [Arguments("nothing here", "needle", 0, "no match keeps the head")]
    public async Task MatchAt_finds_where_the_server_matched(string text, string query, int expected, string because) =>
        await Assert.That(JudgeText.MatchAt(text, query)).IsEqualTo(expected).Because(because);

    [Test]
    public async Task A_cut_never_splits_a_surrogate_pair() {
        var text = "ab\U0001F600cd";

        await Assert.That(JudgeText.Boundary(text, 3)).IsEqualTo(2);
        await Assert.That(JudgeText.Boundary(text, 4)).IsEqualTo(4);
    }
}
