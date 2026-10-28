using Capacitor.Cli.Core.Pages;

namespace Capacitor.Cli.Core.Tests.Unit.Pages;

public class PageTextTests {
    [Test]
    public async Task What_a_person_reads_survives_and_what_runs_or_styles_it_does_not() {
        const string html = """
            <!doctype html><html><head><title>T</title><style>body { color: red }</style></head>
            <body>
              <h1>Plan &amp; scope</h1>
              <!-- a note to self -->
              <p>First <b>bold</b>
                 line.</p>
              <script>alert("hi")</script>
              <ul><li>one</li><li>two</li></ul>
              <img src="data:image/png;base64,AAAA" alt="chart">
            </body></html>
            """;

        await Assert.That(PageText.FromHtml(html)).IsEqualTo("Plan & scope\n\nFirst bold line.\n\none\n\ntwo");
    }

    [Test]
    public async Task A_table_row_keeps_its_cells_apart() =>
        await Assert.That(PageText.FromHtml("<table><tr> <th>A</th>\n <th>B</th> </tr><tr><td>1</td><td>2</td></tr></table>"))
                    .IsEqualTo("A\tB\n\n1\t2");

    [Test]
    public async Task A_page_that_is_not_html_comes_back_as_its_text() =>
        await Assert.That(PageText.FromHtml("just words <unclosed")).IsEqualTo("just words <unclosed");
}
