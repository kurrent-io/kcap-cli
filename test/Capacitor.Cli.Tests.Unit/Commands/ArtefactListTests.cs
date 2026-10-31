using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class ArtefactListTests {
    static ArtefactDto Page(string id, string visibility, string title) =>
        new(id, title, "github:1", visibility, 3, new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.Zero), true, "https://k/p/" + id);

    /// <summary>Under a header, a visibility of <c>none</c> reads as one rather than as a missing value.</summary>
    [Test]
    public async Task The_listing_has_a_header_and_each_value_sits_under_it() {
        string[] lines = [.. ArtefactCommand.ListLines([Page("art_01J9X", "none", "Migration plan"), Page("art_2", "org", "Report")])];

        await Assert.That(lines[0]).StartsWith("PAGE");
        await Assert.That(lines[0]).Contains("VISIBILITY");

        var column = lines[0].IndexOf("VISIBILITY", StringComparison.Ordinal);

        await Assert.That(lines[1][column..].StartsWith("none ", StringComparison.Ordinal)).IsTrue();
        await Assert.That(lines[2][column..].StartsWith("org ", StringComparison.Ordinal)).IsTrue();
        await Assert.That(lines[1]).EndsWith("Migration plan");
    }

    /// <summary>A title's line breaks and tabs would otherwise print as rows that are not pages.</summary>
    [Test]
    public async Task A_title_with_line_breaks_stays_on_its_own_row() {
        string[] lines = [.. ArtefactCommand.ListLines([Page("art_1", "org", "Plan\nart_2  v1 org\r\nend\rof\ttitle\u2028and\u2029more\u0085rows")])];

        await Assert.That(lines.Length).IsEqualTo(2);
        await Assert.That(lines[1]).EndsWith("Plan art_2  v1 org end of title and more rows");
    }
}
