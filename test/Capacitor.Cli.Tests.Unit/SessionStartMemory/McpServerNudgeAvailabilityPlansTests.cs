using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Pi;

namespace Capacitor.Cli.Tests.Unit.SessionStartMemory;

// Availability reads the hermetic registry, which consults no override variable — so this needs no
// parallel constraint.
public class McpServerNudgeAvailabilityPlansTests {
    [TempHome] public required TempHome Home { get; init; }

    HarnessRegistry Harnesses => TestHarnesses.Under(Home);

    [Test]
    public async Task Pi_extension_listing_plans_is_available_and_one_without_is_not() {
        var extension = Path.GetRelativePath(Home.Path, Harnesses.Of<PiHarness>().Paths.KcapMcpExtension);

        Home.CreateFile(extension, """const KCAP_MCP_SERVERS = ["review", "workitems", "plans"];""");
        await Assert.That(McpServerNudgeAvailability.IsRegisteredFor(HarnessId.Pi, Harnesses, "kcap-plans")).IsTrue();

        Home.CreateFile(extension, """const KCAP_MCP_SERVERS = ["review", "workitems"];""");
        await Assert.That(McpServerNudgeAvailability.IsRegisteredFor(HarnessId.Pi, Harnesses, "kcap-plans")).IsFalse();
        await Assert.That(McpServerNudgeAvailability.IsRegisteredFor(HarnessId.Pi, Harnesses, "kcap-workitems")).IsTrue();
    }

    /// <summary>An enabled plugin whose materialized .mcp.json predates kcap-plans must not nudge
    /// toward it, while the server it does carry stays available.</summary>
    [Test]
    public async Task Claude_requires_the_server_in_the_installed_plugins_mcp_json() {
        var claude  = Home.CreateDir(".claude");
        var plugins = claude.CreateDir("plugins");
        var install = plugins.CreateDir("cache", "kcap");

        claude.CreateFile("settings.json", """{"enabledPlugins":{"kcap@kcap":true}}""");
        plugins.CreateFile("installed_plugins.json", new JsonObject {
            ["plugins"] = new JsonObject {
                ["kcap@kcap"] = new JsonArray(new JsonObject { ["scope"] = "user", ["installPath"] = install.Path })
            }
        }.ToJsonString());
        install.CreateFile(".mcp.json", """{"mcpServers":{"kcap-workitems":{"command":"kcap","args":["mcp","workitems"]}}}""");

        await Assert.That(McpServerNudgeAvailability.IsRegisteredFor(HarnessId.Claude, Harnesses, "kcap-workitems")).IsTrue();
        await Assert.That(McpServerNudgeAvailability.IsRegisteredFor(HarnessId.Claude, Harnesses, "kcap-plans")).IsFalse();

        install.CreateFile(".mcp.json", """{"mcpServers":{"kcap-workitems":{},"kcap-plans":{"command":"kcap","args":["mcp","plans"]}}}""");
        await Assert.That(McpServerNudgeAvailability.IsRegisteredFor(HarnessId.Claude, Harnesses, "kcap-plans")).IsTrue();
    }

    [Test]
    public async Task Claude_without_an_installed_payload_suppresses_every_server() {
        Home.CreateDir(".claude").CreateFile("settings.json", """{"enabledPlugins":{"kcap@kcap":true}}""");

        await Assert.That(McpServerNudgeAvailability.IsRegisteredFor(HarnessId.Claude, Harnesses, "kcap-plans")).IsFalse();
        await Assert.That(McpServerNudgeAvailability.IsRegisteredFor(HarnessId.Claude, Harnesses, "kcap-workitems")).IsFalse();
    }
}
