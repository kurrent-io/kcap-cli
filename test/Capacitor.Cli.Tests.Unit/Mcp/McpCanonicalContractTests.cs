using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Mcp;

namespace Capacitor.Cli.Tests.Unit.Mcp;

public class McpCanonicalContractTests {
    // Resolve the repo's kcap/ dir relative to the test assembly (…/src/cli).
    static string KcapDir() {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (; d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "kcap")) &&
                File.Exists(Path.Combine(d.FullName, "kcap", ".mcp.json")))
                return Path.Combine(d.FullName, "kcap");
        throw new DirectoryNotFoundException("kcap/ not found above test base dir");
    }

    static string[] Keys(string file) =>
        [.. ((JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(KcapDir(), file)))!["mcpServers"]!)
            .Select(kv => kv.Key)];

    [Test]
    public async Task Bundled_claude_mcp_json_matches_the_canonical_list() {
        await Assert.That(Keys(".mcp.json"))
            .IsEquivalentTo(KcapMcpServers.All.Select(s => s.Name).ToArray());
    }

    [Test]
    public async Task Bundled_codex_mcp_json_matches_the_codex_subset() {
        await Assert.That(Keys(".codex-mcp.json"))
            .IsEquivalentTo(KcapMcpServers.ForCodex.Select(s => s.Name).ToArray());
    }

    [Test]
    public async Task Codex_subset_excludes_flows_but_keeps_memory() {
        var names = KcapMcpServers.ForCodex.Select(s => s.Name).ToArray();
        await Assert.That(names).DoesNotContain("kcap-flows");
        await Assert.That(names).Contains("kcap-memory");
    }
}
