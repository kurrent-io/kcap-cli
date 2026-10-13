using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Mcp;

namespace Capacitor.Cli.Core.Tests.Unit.Mcp;

/// <summary>
/// <c>kcap refresh</c> points the shipped plugin's MCP entries at the native binary. Only canonical
/// name/args pairs whose command is the shipped literal or a stale kcap path change; the rest of the
/// file survives, and a file that is not the plugin config is left as it was.
/// </summary>
public class PluginMcpConfigPatcherTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string Binary = "/home/u/.local/share/kcap/current/bin/kcap";

    /// <summary>The real plugin config, so the canonical entries are the ones that ship.</summary>
    static string ShippedMcpJson() {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "kcap", ".mcp.json"))) dir = Path.GetDirectoryName(dir);
        return File.ReadAllText(Path.Combine(dir!, "kcap", ".mcp.json"));
    }

    [Test]
    public async Task Every_shipped_server_is_pointed_at_the_binary() {
        Tmp.CreateFile("plugin/.mcp.json", ShippedMcpJson());

        var outcome = PluginMcpConfigPatcher.Patch(Tmp.PathTo("plugin"), Binary, out var error);

        await Assert.That(outcome).IsEqualTo(PluginMcpConfigPatcher.Outcome.Patched);
        await Assert.That(error).IsNull();
        var servers = JsonNode.Parse(File.ReadAllText(Tmp.PathTo("plugin", ".mcp.json")))!["mcpServers"]!.AsObject();
        foreach (var (_, entry) in servers)
            await Assert.That(entry!["command"]!.GetValue<string>()).IsEqualTo(Binary);
        await Assert.That(servers.Count).IsEqualTo(KcapMcpServers.All.Count);
    }

    [Test]
    public async Task A_second_run_changes_nothing() {
        Tmp.CreateFile("plugin/.mcp.json", ShippedMcpJson());
        PluginMcpConfigPatcher.Patch(Tmp.PathTo("plugin"), Binary, out _);

        await Assert.That(PluginMcpConfigPatcher.Patch(Tmp.PathTo("plugin"), Binary, out _))
            .IsEqualTo(PluginMcpConfigPatcher.Outcome.Unchanged);
    }

    [Test]
    public async Task Customized_entries_and_foreign_servers_are_kept() {
        var cfg = new JsonObject {
            ["mcpServers"] = new JsonObject {
                ["kcap-review"]   = new JsonObject { ["command"] = "/old/bin/kcap.exe", ["args"] = new JsonArray("mcp", "review") },
                ["kcap-sessions"] = new JsonObject { ["command"] = "npx", ["args"] = new JsonArray("mcp", "sessions") },
                ["kcap-memory"]   = new JsonObject { ["command"] = "kcap", ["args"] = new JsonArray("mcp", "memory", "--x") },
                ["other"]         = new JsonObject { ["command"] = "kcap", ["args"] = new JsonArray("mcp", "review") },
            },
        };

        var changed = PluginMcpConfigPatcher.PatchServers(cfg, Binary);

        await Assert.That(changed).IsTrue();
        var servers = cfg["mcpServers"]!;
        await Assert.That(servers["kcap-review"]!["command"]!.GetValue<string>()).IsEqualTo(Binary);
        await Assert.That(servers["kcap-sessions"]!["command"]!.GetValue<string>()).IsEqualTo("npx");
        await Assert.That(servers["kcap-memory"]!["command"]!.GetValue<string>()).IsEqualTo("kcap");
        await Assert.That(servers["other"]!["command"]!.GetValue<string>()).IsEqualTo("kcap");
    }

    [Test]
    [Arguments("kcap", true)]
    [Arguments("/old/prefix/bin/kcap", true)]
    [Arguments(@"C:\old\npm\bin\kcap.exe", true)]
    [Arguments("/old/prefix/bin/KCAP.EXE", true)]
    [Arguments("npx", false)]
    [Arguments("bin/kcap", false)]
    [Arguments("/usr/bin/node", false)]
    public async Task Only_the_literal_or_an_absolute_kcap_path_is_patchable(string command, bool expected) {
        await Assert.That(PluginMcpConfigPatcher.IsPatchable(command)).IsEqualTo(expected);
    }

    [Test]
    public async Task A_file_that_is_not_the_plugin_config_fails_and_is_left_alone() {
        var path = Tmp.CreateFile("plugin/.mcp.json", """{"mcpServers":[]}""");

        var outcome = PluginMcpConfigPatcher.Patch(Tmp.PathTo("plugin"), Binary, out var error);

        await Assert.That(outcome).IsEqualTo(PluginMcpConfigPatcher.Outcome.Failed);
        await Assert.That(error).IsNotNull();
        await Assert.That(File.ReadAllText(path)).IsEqualTo("""{"mcpServers":[]}""");
    }

    [Test]
    public async Task No_plugin_config_is_missing() {
        await Assert.That(PluginMcpConfigPatcher.Patch(Tmp.PathTo("nowhere"), Binary, out _))
            .IsEqualTo(PluginMcpConfigPatcher.Outcome.Missing);
    }
}
