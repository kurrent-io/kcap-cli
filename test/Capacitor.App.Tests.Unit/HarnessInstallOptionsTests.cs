using Capacitor.App.Services;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.App.Tests.Unit;

public class HarnessInstallOptionsTests {
    /// The install ignores a skip flag it does not take, so a misnamed one leaves that part of the
    /// tools installed with no error; these are the flags each vendor's install reads.
    [Test]
    [Arguments("Cursor", "--skip-cursor-mcp --skip-cursor-skills")]
    [Arguments("Kiro", "--skip-kiro-mcp --skip-kiro-skills")]
    [Arguments("MistralVibe", "--skip-mistral-vibe-mcp --skip-mistral-vibe-skills")]
    [Arguments("Muse", "--skip-muse-mcp --skip-muse-skills")]
    [Arguments("Grok", "--skip-grok-skills")]
    [Arguments("Gemini", "--skip-gemini-mcp --skip-gemini-skills --skip-gemini-instructions")]
    [Arguments("Copilot", "--skip-copilot-mcp --skip-copilot-skills --skip-copilot-instructions")]
    public async Task Recording_without_tools_skips_exactly_the_vendors_tool_parts(string vendor, string expected) =>
        await Assert.That(string.Join(' ', HarnessInstallOptions.For(Enum.Parse<HarnessId>(vendor), record: true, tools: false)))
            .IsEqualTo(expected);

    [Test]
    public async Task Bundled_vendors_take_no_options_and_tools_alone_ask_for_tools_only() {
        await Assert.That(HarnessInstallOptions.For(HarnessId.Codex, record: true, tools: false)).IsEmpty();
        await Assert.That(HarnessInstallOptions.For(HarnessId.Cursor, record: false, tools: true)).IsEquivalentTo(new[] { "--tools-only" });
    }
}
