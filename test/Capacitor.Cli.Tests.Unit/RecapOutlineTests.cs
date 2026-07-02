using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit;

public class RecapOutlineTests {
    [Test]
    public async Task FormatTurnOutline_uses_prose_line_when_present() {
        const string turns = """
            [ {"turn_index":0,"user_prompt":"add feature","prose":"Added the feature and tests.","tools":[{"name":"Edit"}],"files":[{"path":"a.cs"}]} ]
            """;

        var outline = RecapCommand.FormatTurnOutline(turns);

        await Assert.That(outline).Contains("## Turns");
        await Assert.That(outline).Contains("Added the feature and tests.");
        await Assert.That(outline).Contains("0");
    }

    [Test]
    public async Task FormatTurnOutline_falls_back_to_prompt_and_metadata_when_prose_absent() {
        const string turns = """
            [ {"turn_index":2,"user_prompt":"investigate the failing build and fix it","prose":null,"tools":[{"name":"Bash"},{"name":"Edit"}],"files":[{"path":"a.cs"},{"path":"b.cs"}]} ]
            """;

        var outline = RecapCommand.FormatTurnOutline(turns);

        await Assert.That(outline).Contains("investigate the failing build");
        await Assert.That(outline).Contains("Bash");
        await Assert.That(outline).Contains("Edit");
        await Assert.That(outline).Contains("2 files");
    }

    [Test]
    public async Task FormatTurnOutline_truncates_long_prompt_in_fallback() {
        var longPrompt = new string('x', 200);
        var turns      = $$"""
            [ {"turn_index":0,"user_prompt":"{{longPrompt}}","prose":null,"tools":[],"files":[]} ]
            """;

        var outline = RecapCommand.FormatTurnOutline(turns);

        await Assert.That(outline).Contains("…");
        await Assert.That(outline.Length).IsLessThan(200);
    }

    [Test]
    public async Task FormatTurnOutline_returns_empty_for_empty_array() {
        await Assert.That(RecapCommand.FormatTurnOutline("[]")).IsEqualTo("");
    }

    [Test]
    public async Task FormatTurnOutline_returns_empty_for_non_json_body() {
        await Assert.That(RecapCommand.FormatTurnOutline("not json")).IsEqualTo("");
    }

    [Test]
    public async Task FormatTurnOutline_returns_empty_for_empty_body() {
        await Assert.That(RecapCommand.FormatTurnOutline("")).IsEqualTo("");
    }

    [Test]
    public async Task ExtractToolNames_dedupes_and_preserves_order() {
        using var doc = System.Text.Json.JsonDocument.Parse(
            """{"tools":[{"name":"Edit"},{"name":"Bash"},{"name":"Edit"}]}""");

        var names = RecapCommand.ExtractToolNames(doc.RootElement);

        await Assert.That(names).IsEquivalentTo(new List<string> { "Edit", "Bash" });
    }
}
