using System.Text.Json;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// The bridge's tool-settled notice reaches the broker with every scope it carries. Claude's
/// permission hook raises a prompt without a tool-use id, so a notice that names the tool by name
/// and input must retire that prompt alone, not fall through to the whole turn.
public class AgentOrchestratorToolSettledTests {
    static JsonElement Input(string json) {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    static PermissionPendingDto Prompt(string id, string inputJson, string requestedAt) =>
        new(id, "a1", "s1", "claude", "Bash", Input(inputJson), null, false, false, requestedAt);

    [Test]
    public async Task A_notice_naming_a_tool_by_name_and_input_retires_that_prompt_only() {
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        var broker = orch.PermissionBrokerForTest;
        var ls = broker.Register(Prompt("r1", """{"command":"ls"}""", "2026-10-18T10:00:00.0000000+00:00"));
        var rm = broker.Register(Prompt("r2", """{"command":"rm x"}""", "2026-10-18T10:00:01.0000000+00:00"));

        orch.PermissionBridgeForTest.ToolSettledHandler!("a1", new ToolSettledNotice("s1", "toolu_1", null, "Bash", Input("""{"command":"ls"}""")));

        await Assert.That(ls.IsCompleted).IsTrue();
        await Assert.That((await ls).Outcome).IsEqualTo("withdrawn");
        await Assert.That(rm.IsCompleted).IsFalse();
        await Assert.That(broker.PendingSnapshot().Single().RequestId).IsEqualTo("r2");
    }
}
