using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Kiro;

namespace Capacitor.Cli.Tests.Unit.SessionStartMemory;

/// <summary>The on-disk config names every server in each test, so only the session asking can
/// change the answer.</summary>
public class McpServerNudgeAvailabilitySessionTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempDir]  public required TempDir  Tmp  { get; init; }

    HarnessRegistry Harnesses => TestHarnesses.Under(Home);

    static readonly SessionStartAudience Participant = new(IsHosted: true, IsFlowParticipant: true);
    static readonly SessionStartAudience Hosted      = new(IsHosted: true, IsFlowParticipant: false);

    void InstallClaudePlugin(params string[] servers) {
        var claude  = Home.CreateDir(".claude");
        var plugins = claude.CreateDir("plugins");
        var install = plugins.CreateDir("cache", "kcap", "kcap", "1.0.0");

        claude.CreateFile("settings.json", """{"enabledPlugins":{"kcap@kcap":true}}""");
        plugins.CreateFile("installed_plugins.json", new JsonObject {
            ["plugins"] = new JsonObject {
                ["kcap@kcap"] = new JsonArray(new JsonObject { ["scope"] = "user", ["installPath"] = install.Path, ["version"] = "1.0.0" })
            }
        }.ToJsonString());
        install.CreateFile(".mcp.json", "{\"mcpServers\":{" +
            string.Join(",", servers.Select(s => $"\"{s}\":{{\"command\":\"kcap\"}}")) + "}}");
    }

    /// <summary>A review-flow reviewer runs under a strict MCP config, so the plugin's servers are
    /// on disk yet never loaded into it.</summary>
    [Test]
    public async Task A_flow_participant_has_none_of_the_plugins_servers() {
        InstallClaudePlugin("kcap-workitems", "kcap-plans");

        await Assert.That(McpServerNudgeAvailability.IsRegisteredFor(HarnessId.Claude, Harnesses, "kcap-workitems")).IsTrue();
        await Assert.That(McpServerNudgeAvailability.IsRegisteredFor(HarnessId.Claude, Harnesses, "kcap-workitems", audience: Participant)).IsFalse();
        await Assert.That(McpServerNudgeAvailability.IsRegisteredFor(HarnessId.Claude, Harnesses, "kcap-plans", audience: Participant)).IsFalse();
    }

    [Test]
    public async Task A_hosted_agent_is_decided_by_the_config_it_loads() {
        InstallClaudePlugin("kcap-workitems");

        await Assert.That(McpServerNudgeAvailability.IsRegisteredFor(HarnessId.Claude, Harnesses, "kcap-workitems", audience: Hosted)).IsTrue();
        await Assert.That(McpServerNudgeAvailability.IsRegisteredFor(HarnessId.Claude, Harnesses, "kcap-plans", audience: Hosted)).IsFalse();
    }

    [Test]
    public async Task The_rule_holds_for_a_harness_reading_its_own_settings() {
        Home.CreateFile(Path.GetRelativePath(Home.Path, Harnesses.Of<KiroHarness>().Paths.SettingsMcpJson),
            """{"mcpServers":{"kcap-plans":{"command":"kcap","args":["mcp","plans"]}}}""");

        await Assert.That(McpServerNudgeAvailability.IsRegisteredFor(HarnessId.Kiro, Harnesses, "kcap-plans", audience: Hosted)).IsTrue();
        await Assert.That(McpServerNudgeAvailability.IsRegisteredFor(HarnessId.Kiro, Harnesses, "kcap-plans", audience: Participant)).IsFalse();
    }

    [Test]
    public async Task A_flow_participant_gets_no_session_nudges_at_all() {
        var codexConfig = Tmp.CreateFile("config.toml", string.Concat(new[] { "workitems", "plans", "flows" }.Select(s =>
            $"[mcp_servers.kcap-{s}]\ncommand = \"kcap\"\nargs = [\"mcp\", \"{s}\"]\n\n")));

        await Assert.That(SessionNudges.Resolve(HarnessId.Codex, "s1", null, Harnesses, PlanEntitlements.Unknown, Hosted, cwd: null, Home, codexConfig)).IsNotNull();
        await Assert.That(SessionNudges.Resolve(HarnessId.Codex, "s1", null, Harnesses, PlanEntitlements.Unknown, Participant, cwd: null, Home, codexConfig)).IsNull();
    }
}
