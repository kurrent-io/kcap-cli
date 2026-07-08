using Capacitor.Cli.Core.Copilot;

namespace Capacitor.Cli.Tests.Unit;

public class CopilotPathsTests {
    [Test]
    public async Task McpConfigJson_is_mcp_config_json_under_copilot_root() {
        await Assert.That(CopilotPaths.McpConfigJson("/h"))
            .IsEqualTo(Path.Combine("/h", ".copilot", "mcp-config.json"));
    }

    [Test]
    public async Task McpConfigJson_honors_copilot_home_override() {
        // $COPILOT_HOME (passed here as the copilotHome arg) replaces the entire
        // ~/.copilot path, so the file sits directly under it.
        await Assert.That(CopilotPaths.McpConfigJson("/h", "/custom/loc"))
            .IsEqualTo(Path.Combine("/custom/loc", "mcp-config.json"));
    }
}
