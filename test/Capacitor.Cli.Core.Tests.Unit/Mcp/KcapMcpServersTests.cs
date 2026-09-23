using Capacitor.Cli.Core.Mcp;

namespace Capacitor.Cli.Core.Tests.Unit.Mcp;

public class KcapMcpServersTests {
    [Test]
    public async Task All_contains_the_eight_canonical_servers() {
        var names = KcapMcpServers.All.Select(s => s.Name).ToArray();
        await Assert.That(names).IsEquivalentTo(new[] { "kcap-review", "kcap-sessions", "kcap-flows", "kcap-memory", "kcap-workitems", "kcap-plans", "kcap-artefacts", "kcap-analytics" });
    }

    [Test]
    public async Task ForCodex_is_the_full_set_including_workitems() {
        // kcap-workitems is now registered on every harness, so the Codex subset is All.
        var names = KcapMcpServers.ForCodex.Select(s => s.Name).ToArray();
        await Assert.That(names).IsEquivalentTo(new[] { "kcap-review", "kcap-sessions", "kcap-flows", "kcap-memory", "kcap-workitems", "kcap-plans", "kcap-artefacts", "kcap-analytics" });
    }

    [Test]
    public async Task ForCursor_is_the_full_set_including_workitems() {
        // every non-Claude JSON harness now receives kcap-workitems too.
        var names = KcapMcpServers.ForCursor.Select(s => s.Name).ToArray();
        await Assert.That(names).IsEquivalentTo(new[] { "kcap-review", "kcap-sessions", "kcap-flows", "kcap-memory", "kcap-workitems", "kcap-plans", "kcap-artefacts", "kcap-analytics" });
    }

    [Test]
    public async Task ForHarness_stamps_only_the_flows_entry_with_the_driver_vendor() {
        var servers = KcapMcpServers.ForHarness("cursor");

        // Same servers, same order as the bare set — only flows' args change.
        await Assert.That(servers.Select(s => s.Name).ToArray())
            .IsEquivalentTo(KcapMcpServers.ForCursor.Select(s => s.Name).ToArray());

        var flows = servers.Single(s => s.Name == "kcap-flows");
        await Assert.That(flows.Args).IsEquivalentTo(new[] { "mcp", "flows", "--driver", "cursor" });

        // Every non-flows server is byte-identical to the bare set (no accidental stamp elsewhere).
        foreach (var s in servers.Where(s => s.Name != "kcap-flows")) {
            var bare = KcapMcpServers.ForCursor.Single(b => b.Name == s.Name);
            await Assert.That(s.Args).IsEquivalentTo(bare.Args);
        }
    }

    [Test]
    public async Task ForHarness_leaves_the_bare_All_list_unstamped() {
        // ForHarness must not mutate the shared descriptors — the audit/registry read All as the
        // canonical prefix, so a leaked stamp there would misclassify every unstamped entry.
        _ = KcapMcpServers.ForHarness("kiro");
        var flows = KcapMcpServers.All.Single(s => s.Name == "kcap-flows");
        await Assert.That(flows.Args).IsEquivalentTo(new[] { "mcp", "flows" });
    }

    [Test]
    public async Task Review_is_the_only_non_repo_scoped_server() {
        var repoScoped = KcapMcpServers.All.Where(s => s.NeedsProjectCwd).Select(s => s.Name).ToArray();
        await Assert.That(repoScoped).IsEquivalentTo(new[] { "kcap-sessions", "kcap-flows", "kcap-memory", "kcap-workitems", "kcap-plans", "kcap-artefacts", "kcap-analytics" });
    }

    [Test]
    public async Task Auto_approve_covers_reads_and_own_record_writers_only() {
        // AutoApprove drives per-server trust on Codex and Gemini. kcap-flows launches a paid hosted
        // agent and kcap-artefacts can widen who may open a page, so both must keep prompting.
        var approved = KcapMcpServers.All.Where(s => s.AutoApprove).Select(s => s.Name).ToArray();
        await Assert.That(approved).IsEquivalentTo(new[] {
            "kcap-review", "kcap-sessions", "kcap-analytics", "kcap-memory", "kcap-workitems", "kcap-plans"
        });
    }

    [Test]
    public async Task Flows_is_the_only_server_whose_tool_calls_block_for_minutes() {
        var timed = KcapMcpServers.All.Where(s => s.ToolTimeout is not null).ToArray();
        await Assert.That(timed.Select(s => s.Name).ToArray()).IsEquivalentTo(new[] { "kcap-flows" });
        await Assert.That(timed[0].ToolTimeout).IsEqualTo(TimeSpan.FromMinutes(10));
    }
}
