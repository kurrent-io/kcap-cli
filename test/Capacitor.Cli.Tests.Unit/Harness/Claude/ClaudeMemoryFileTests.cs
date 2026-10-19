using Capacitor.Cli.Harness.Claude;

namespace Capacitor.Cli.Tests.Unit.Harness.Claude;

public class ClaudeMemoryFileTests {
    [Test]
    public async Task Reads_the_type_nested_under_metadata() {
        var file = ClaudeMemoryFile.Parse("a.md", """
            ---
            name: utc-clock
            description: Servers run UTC
            metadata:
              type: feedback
              originSessionId: e381b4ee
            ---

            Convert before comparing.
            """);

        await Assert.That(file.Name).IsEqualTo("utc-clock");
        await Assert.That(file.Description).IsEqualTo("Servers run UTC");
        await Assert.That(file.Type).IsEqualTo("feedback");
        await Assert.That(file.Body.Trim()).IsEqualTo("Convert before comparing.");
    }

    [Test]
    public async Task Reads_a_top_level_type_quoted_values_and_crlf() {
        var file = ClaudeMemoryFile.Parse("a.md",
            "\uFEFF---\r\nname: 'it''s'\r\ndescription: \"a: \\\"quoted\\\" line\"\r\ntype: user\r\n---\r\nbody\r\n");

        await Assert.That(file.Name).IsEqualTo("it's");
        await Assert.That(file.Description).IsEqualTo("a: \"quoted\" line");
        await Assert.That(file.Type).IsEqualTo("user");
        await Assert.That(file.Body.Trim()).IsEqualTo("body");
    }

    [Test]
    public async Task Folds_a_block_scalar_description() {
        var file = ClaudeMemoryFile.Parse("a.md", """
            ---
            name: n
            description: >
              first half
              second half
            type: project
            ---
            b
            """);

        await Assert.That(file.Description).IsEqualTo("first half second half");
        await Assert.That(file.Type).IsEqualTo("project");
    }

    [Test]
    [Arguments("just a body")]
    [Arguments("---\nname: unterminated\nbody")]
    public async Task Text_without_closed_frontmatter_is_all_body(string text) {
        var file = ClaudeMemoryFile.Parse("a.md", text);

        await Assert.That(file.Name).IsNull();
        await Assert.That(file.Body).IsEqualTo(text);
    }
}
