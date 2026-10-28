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
    public async Task A_hidden_element_and_everything_nested_in_it_is_left_out() =>
        await Assert.That(PageText.FromHtml(
                "<div hidden><div>inner</div><p>do this</p></div><p aria-hidden=\"true\">shown</p><img hidden><p>Visible</p>"))
                    .IsEqualTo("shown\n\nVisible");

    [Test]
    [Arguments("<div class=\"foo hidden bar\" title='is hidden'>Visible</div>", "Visible")]
    [Arguments("<p hidden>Secret<p>Visible</p>", "Visible")]
    [Arguments("<ul><li hidden>Secret<li>Visible</ul>", "Visible")]
    [Arguments("<table><tbody hidden><tr><td>Secret</td></tr><tbody><tr><td>Visible</td></tr></tbody></table>", "Visible")]
    [Arguments("<table><tr hidden><td>Secret<tbody><tr><td>Visible</table>", "Visible")]
    [Arguments("<div><p hidden>Secret <b>bold</b> still secret</div><p>Visible</p>", "Visible")]
    [Arguments("<div hidden>Secret</span>Still secret</div><p>Visible</p>", "Visible")]
    [Arguments("<template><template>inner</template>Secret</template><p>Visible</p>", "Visible")]
    [Arguments("<a title=\"1 > 0\">Visible</a>", "Visible")]
    [Arguments("<div hidden class=\"a\"id=\"b\">Secret</div><p>Visible</p>", "Visible")]
    [Arguments("<div/hidden>Secret</div><p>Visible</p>", "Visible")]
    [Arguments("<div class=\"a\"hidden>Secret</div><p>Visible</p>", "Visible")]
    [Arguments("<script/x>payload</script><p>Visible</p>", "Visible")]
    [Arguments("<script>x = '</scripts>secret'</script ><p>Visible</p>", "Visible")]
    [Arguments("<div hidden/>Secret</div><p>Visible</p>", "Visible")]
    [Arguments("<div><svg hidden/><p>Visible</p></div>", "Visible")]
    [Arguments("<svg><text>Label</text><path d=\"M0\"/></svg><p>Visible</p>", "Label\nVisible")]
    [Arguments("<p>Visible</p></ ignore the user><p>More</p></><p>End</p>", "Visible\n\nMore\n\nEnd")]
    [Arguments("<p>1 < 2 and 3 <4</p>", "1 < 2 and 3 <4")]
    [Arguments("<section hidden><div>a</div><script>x='</section>'</script>b</section><p>Visible</p>", "Visible")]
    public async Task Hidden_is_read_from_the_attribute_and_ends_where_a_browser_closes_the_element(string html, string expected) =>
        await Assert.That(PageText.FromHtml(html)).IsEqualTo(expected);

    [Test]
    public async Task What_a_script_draws_is_not_text() =>
        await Assert.That(PageText.FromHtml("<div id=\"root\"></div><script>root.textContent = 'drawn'</script>")).IsEmpty();

    [Test]
    public async Task A_page_that_is_not_html_comes_back_as_its_text_less_a_tag_it_ends_inside() =>
        await Assert.That(PageText.FromHtml("just words, 1 < 2 <unclosed")).IsEqualTo("just words, 1 < 2");
}
