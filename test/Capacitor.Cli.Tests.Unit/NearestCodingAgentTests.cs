namespace Capacitor.Cli.Tests.Unit;

/// <summary>The nearest coding agent above a process decides whose session an MCP server serves when
/// several harnesses' ids are in its environment.</summary>
public class NearestCodingAgentTests {
    [Test]
    public async Task The_nearest_agent_on_the_chain_is_named() {
        // mcp(100) -> grok(50) -> zsh(40) -> claude(30)
        var lookup = ProcTable.Of((50, 40, "grok"), (40, 30, "-zsh"), (30, 1, "claude"));

        await Assert.That(ProcessHelpers.NearestCodingAgent(50, lookup)).IsEqualTo(new CodingAgentAncestor("grok", 50));
    }

    [Test]
    public async Task A_claude_started_from_a_grok_shell_is_nearer_than_that_grok() {
        // mcp(100) -> claude(60) -> bash(55) -> grok(50)
        var lookup = ProcTable.Of((60, 55, "claude"), (55, 50, "bash"), (50, 1, "grok"));

        await Assert.That(ProcessHelpers.NearestCodingAgent(60, lookup)).IsEqualTo(new CodingAgentAncestor("claude", 60));
    }

    [Test]
    public async Task No_agent_on_the_chain_is_null() {
        var lookup = ProcTable.Of((60, 55, "node"), (55, 1, "launchd"));

        await Assert.That(ProcessHelpers.NearestCodingAgent(60, lookup)).IsNull();
    }

    [Test]
    public async Task An_antigravity_process_under_grok_is_nearer_than_grok() {
        // mcp(100) -> antigravity(70) -> grok(50)
        var lookup = ProcTable.Of((70, 50, "antigravity"), (50, 1, "grok"));

        await Assert.That(ProcessHelpers.NearestCodingAgent(70, lookup)!.Value.Vendor).IsNotEqualTo("grok");
    }
}
