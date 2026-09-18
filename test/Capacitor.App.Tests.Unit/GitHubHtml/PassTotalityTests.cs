using System.Text;
using Capacitor.App.GitHubHtml;

namespace Capacitor.App.Tests.Unit.GitHubHtml;

public class PassTotalityTests {
    static readonly string[] Pieces = [
        "<b>", "</b>", "<i>", "</i>", "<code>", "</code>", "<a href=\"https://u.example\">", "<a href=\"javascript:x\">", "<a>", "</a>",
        "<img src=\"https://h/i.png\" alt=\"i\">", "<img src=\"https://h/a%0Ab.png\">", "<br>", "<br/>", "</br>",
        "<details>", "<details open>", "</details>", "<summary>", "</summary>", "<pre>", "</pre>", "<pre><code>", "</code></pre>",
        "<span>", "</span>", "<div>", "</div>", "<iframe>", "</iframe>", "<a href=\"", "<!-- c -->", "<!-- open", "<b\n>", "<b/>",
        "<p>", "</p>", "<h2>", "</h2>", "<hr>", "<ul>", "</ul>", "<ol start=\"3\">", "</ol>", "<li>", "</li>", "<dl>", "<dd>", "</dd>", "</dl>",
        "<blockquote>", "</blockquote>", "<table>", "</table>", "<thead>", "</thead>", "<tr>", "</tr>", "<td colspan=\"2\">", "</td>", "<th>", "</th>",
        "<picture><source srcset=\"https://h/d.svg\"><img src=\"https://h/l.svg\"></picture>", "<relative-time datetime=\"2026-01-01\">", "</relative-time>", "<sub>\n\n", "\n\n</sub>",
        "text", "more text", " ", "\n", "\n\n", "&amp;", "&#x27;", "&#10;", "**bold**", "*em*", "`code`", "![alt](https://h/x.png)", "![](https://h/y.png)",
        "<https://example.com>", "[l](https://u.example)", "https://bare.example/x", "> ", "- ", "\n> ", "\n- ", "| a | b |\n|---|---|\n| c | d |\n",
    ];

    /// Deterministic tag soup: random interleavings of inline, block-only, unknown, unclosed and
    /// truncated tags with markdown. Quote and list markers stay shallow so the parsed tree itself
    /// does not exceed the pass's own limit.
    static string Soup(int seed) {
        var random = new Random(seed);
        var builder = new StringBuilder();
        var length = random.Next(1, 400);
        for (var i = 0; i < length; i++) builder.Append(Pieces[random.Next(Pieces.Length)]);
        return builder.ToString();
    }

    /// Calls the pass directly: the extension's boundary catch would hide a throw.
    [Test]
    public async Task Generated_tag_soup_never_throws_and_never_passes_the_depth_limit() {
        for (var seed = 0; seed < 500; seed++) {
            var document = Trees.ParseWithoutPass(Soup(seed));
            GitHubHtmlPass.Run(document);
            await Assert.That(Trees.MaxDepth(document)).IsLessThanOrEqualTo(GitHubHtmlPass.MaxDepth).Because($"seed {seed}");
        }
    }

    [Test]
    public async Task Every_source_tag_left_behind_is_still_a_valid_tree_node() {
        for (var seed = 0; seed < 200; seed++) {
            var document = Trees.Parse(Soup(seed));
            // Dumping walks the whole tree and touches every node's parent links.
            await Assert.That(TreeDump.Of(document)).StartsWith("doc(");
        }
    }

    /// Pins linear work across blocks: thousands of details pairs, each with markdown between,
    /// and thousands of paragraphs of inline tags.
    [Test]
    [Timeout(30_000)]
    public async Task Thousands_of_details_pairs_and_paragraphs_process_in_time(CancellationToken _) {
        var builder = new StringBuilder();
        for (var i = 0; i < 5_000; i++) builder.Append("<details>\n<summary>S").Append(i).Append("</summary>\n\nbody <b>").Append(i).Append("</b> <a href=\"https://u.example/").Append(i).Append("\">l</a>\n\n</details>\n\n");
        var document = Trees.Parse(builder.ToString());
        await Assert.That(TreeDump.Of(document).Split("details#").Length - 1).IsEqualTo(5_000);
    }

    /// Five thousand openers then five thousand closers all match, nested; the cap rejects every
    /// level past the eighth, and propagation must stay linear in the tags.
    [Test]
    [Timeout(30_000)]
    public async Task Thousands_of_nested_details_tags_process_in_time(CancellationToken _) {
        var builder = new StringBuilder();
        for (var i = 0; i < 5_000; i++) builder.Append("<details>\n\n");
        for (var i = 0; i < 5_000; i++) builder.Append("</details>\n\n");
        var document = Trees.Parse(builder.ToString());
        await Assert.That(Trees.MaxDepth(document)).IsLessThanOrEqualTo(GitHubHtmlPass.MaxDepth);
    }
}
