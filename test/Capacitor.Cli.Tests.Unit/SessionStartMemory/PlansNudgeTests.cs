using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Pi;

namespace Capacitor.Cli.Tests.Unit.SessionStartMemory;

public class PlansNudgeEmitterTests {
    [TempHome] public required TempHome Home { get; init; }

    HarnessRegistry Harnesses => TestHarnesses.Under(Home);

    [Test]
    public async Task Build_returns_null_for_a_missing_oversized_or_unsafe_session_id() {
        await Assert.That(PlansNudgeEmitter.Build(null)).IsNull();
        await Assert.That(PlansNudgeEmitter.Build("  ")).IsNull();
        await Assert.That(PlansNudgeEmitter.Build(new string('a', 257))).IsNull();
        await Assert.That(PlansNudgeEmitter.Build("abc`x`")).IsNull();
        await Assert.That(PlansNudgeEmitter.Build("abc\ndef")).IsNull();
    }

    [Test]
    public async Task Build_is_two_sentences_naming_the_four_tools_and_the_session_id() {
        var nudge = PlansNudgeEmitter.Build("s1")!;

        await Assert.That(nudge).StartsWith("## Plans\n");
        await Assert.That(nudge).Contains("declare_plan_document");
        await Assert.That(nudge).Contains("set_plan_tasks");
        await Assert.That(nudge).Contains("update_plan_task");
        await Assert.That(nudge).Contains("get_plan");
        await Assert.That(nudge).Contains("`s1`");

        var body = nudge["## Plans\n".Length..];
        await Assert.That(body.Split(". ").Length).IsEqualTo(2);
        await Assert.That(body).EndsWith(".");
    }

    static string CodexConfigWithPlans(TempDir tmp) =>
        tmp.CreateFile("config.toml", "[mcp_servers.kcap-plans]\ncommand = \"kcap\"\nargs = [\"mcp\", \"plans\"]\n");

    [Test]
    public async Task Resolve_returns_null_when_opted_out() {
        using var tmp = new TempDir();
        await Assert.That(PlansNudgeEmitter.Resolve(HarnessId.Codex, "s1", optedOut: true, Harnesses, CodexConfigWithPlans(tmp))).IsNull();
    }

    [Test]
    public async Task Resolve_returns_the_nudge_when_codex_registers_kcap_plans() {
        using var tmp = new TempDir();
        var nudge = PlansNudgeEmitter.Resolve(HarnessId.Codex, "s1", optedOut: false, Harnesses, CodexConfigWithPlans(tmp));
        await Assert.That(nudge).IsNotNull();
        await Assert.That(nudge!).Contains("`s1`");
    }

    [Test]
    public async Task Resolve_returns_null_when_codex_registers_only_workitems() {
        using var tmp = new TempDir();
        var config = tmp.CreateFile("config.toml", "[mcp_servers.kcap-workitems]\ncommand = \"kcap\"\nargs = [\"mcp\", \"workitems\"]\n");
        await Assert.That(PlansNudgeEmitter.Resolve(HarnessId.Codex, "s1", optedOut: false, Harnesses, config)).IsNull();
    }
}

// Availability reads the hermetic registry, which consults no override variable — so this needs no
// parallel constraint.
public class McpServerNudgeAvailabilityPlansTests {
    [TempHome] public required TempHome Home { get; init; }

    HarnessRegistry Harnesses => TestHarnesses.Under(Home);

    [Test]
    public async Task Pi_extension_listing_plans_is_available_and_one_without_is_not() {
        var path = Harnesses.Of<PiHarness>().Paths.KcapMcpExtension;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await File.WriteAllTextAsync(path, """const KCAP_MCP_SERVERS = ["review", "workitems", "plans"];""");
        await Assert.That(McpServerNudgeAvailability.IsRegisteredFor(HarnessId.Pi, Harnesses, "kcap-plans")).IsTrue();

        await File.WriteAllTextAsync(path, """const KCAP_MCP_SERVERS = ["review", "workitems"];""");
        await Assert.That(McpServerNudgeAvailability.IsRegisteredFor(HarnessId.Pi, Harnesses, "kcap-plans")).IsFalse();
        await Assert.That(McpServerNudgeAvailability.IsRegisteredFor(HarnessId.Pi, Harnesses, "kcap-workitems")).IsTrue();
    }

    /// <summary>An enabled plugin whose materialized .mcp.json predates kcap-plans must not nudge
    /// toward it, while the server it does carry stays available.</summary>
    [Test]
    public async Task Claude_requires_the_server_in_the_installed_plugins_mcp_json() {
        var settings   = Harnesses.Of<ClaudeHarness>().Paths.UserSettings;
        var claudeHome = Path.GetDirectoryName(settings)!;
        var pluginsDir = Path.Combine(claudeHome, "plugins");
        var install    = Path.Combine(pluginsDir, "cache", "kcap");
        Directory.CreateDirectory(install);

        await File.WriteAllTextAsync(settings, """{"enabledPlugins":{"kcap@kcap":true}}""");
        var installed = new JsonObject {
            ["plugins"] = new JsonObject {
                ["kcap@kcap"] = new JsonArray(new JsonObject { ["scope"] = "user", ["installPath"] = install })
            }
        };
        await File.WriteAllTextAsync(Path.Combine(pluginsDir, "installed_plugins.json"), installed.ToJsonString());
        await File.WriteAllTextAsync(Path.Combine(install, ".mcp.json"),
            """{"mcpServers":{"kcap-workitems":{"command":"kcap","args":["mcp","workitems"]}}}""");

        await Assert.That(McpServerNudgeAvailability.IsRegisteredFor(HarnessId.Claude, Harnesses, "kcap-workitems")).IsTrue();
        await Assert.That(McpServerNudgeAvailability.IsRegisteredFor(HarnessId.Claude, Harnesses, "kcap-plans")).IsFalse();

        await File.WriteAllTextAsync(Path.Combine(install, ".mcp.json"),
            """{"mcpServers":{"kcap-workitems":{},"kcap-plans":{"command":"kcap","args":["mcp","plans"]}}}""");
        await Assert.That(McpServerNudgeAvailability.IsRegisteredFor(HarnessId.Claude, Harnesses, "kcap-plans")).IsTrue();
    }

    [Test]
    public async Task Claude_without_an_installed_payload_suppresses_every_server() {
        var settings = Harnesses.Of<ClaudeHarness>().Paths.UserSettings;
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        await File.WriteAllTextAsync(settings, """{"enabledPlugins":{"kcap@kcap":true}}""");

        await Assert.That(McpServerNudgeAvailability.IsRegisteredFor(HarnessId.Claude, Harnesses, "kcap-plans")).IsFalse();
        await Assert.That(McpServerNudgeAvailability.IsRegisteredFor(HarnessId.Claude, Harnesses, "kcap-workitems")).IsFalse();
    }
}
