using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// A hook links its session to the hosted agent only when no other coding agent sits between it and the daemon: a
/// <c>codex exec</c> a hosted Claude runs inherits <c>KCAP_AGENT_ID</c> but its session is its own. Each argument row is
/// the ancestry from the hook's parent upward, one process per entry, its names separated by '|'.
/// </summary>
public class NestedHarnessTests {
    static readonly string[] Agents = [..ProcessHelpers.NestableAgents, "muse"];

    [Test]
    // npm kcap launcher -> sh -> claude -> kcap-daemon
    [Arguments(false, new[] { "node|kcap", "sh", "claude", "kcap-daemon" })]
    // npm kcap launcher -> codex -> zsh -> claude -> kcap: a nested agent behind the outer agent's shell
    [Arguments(true, new[] { "node|kcap", "codex", "zsh", "claude", "kcap" })]
    // codex -> claude -> kcap: a nested agent the outer one started directly
    [Arguments(true, new[] { "codex", "claude", "kcap" })]
    // claude -> claude -> kcap: a same-vendor agent the outer one started directly
    [Arguments(true, new[] { "claude", "claude", "kcap" })]
    // native codex -> codex's npm shim -> kcap: one agent, launched through its own shim
    [Arguments(false, new[] { "codex", "node|codex", "kcap" })]
    // native claude -> claude under node -> kcap: a native agent a Node-run one of the same vendor started directly
    [Arguments(true, new[] { "claude", "node|claude", "kcap" })]
    // gemini -> gemini -> kcap: an agent that runs under node, started directly by another
    [Arguments(true, new[] { "node|gemini", "node|gemini", "kcap" })]
    // codex -> zsh -> muse's versioned binary -> kcap-daemon
    [Arguments(true, new[] { "codex", "zsh", "muse-bin-1.4.7", "kcap-daemon" })]
    // cursor-agent -> zsh -> agy -> kcap: agents named other than their vendor
    [Arguments(true, new[] { "cursor-agent", "zsh", "agy", "kcap" })]
    // claude -> kcap-daemon -> zsh -> codex: a daemon started inside another agent's session
    [Arguments(false, new[] { "claude", "kcap-daemon", "zsh", "codex" })]
    // codex -> zsh -> claude, then unreadable: what was seen still counts
    [Arguments(true, new[] { "codex", "zsh", "claude" })]
    public async Task A_second_coding_agent_below_the_daemon_marks_the_hook_nested(bool nested, string[] ancestry) {
        var rows = ancestry.Select((names, i) => (pid: 100 - i, ppid: i + 1 < ancestry.Length ? 99 - i : 2, names.Split('|'))).ToArray();

        await Assert.That(NestedHarness.IsNested(100, Agents, ProcTable.OfNames(rows))).IsEqualTo(nested);
    }

    /// <summary>However many processes sit between the two agents, the walk reaches the outer one.</summary>
    [Test]
    public async Task A_second_agent_far_up_the_ancestry_still_marks_the_hook_nested() {
        string[] ancestry = ["codex", ..Enumerable.Repeat("sh", 100), "claude", "kcap"];
        var rows = ancestry.Select((names, i) => (pid: 1000 - i, ppid: i + 1 < ancestry.Length ? 999 - i : 2, new[] { names })).ToArray();

        await Assert.That(NestedHarness.IsNested(1000, Agents, ProcTable.OfNames(rows))).IsTrue();
    }

    /// <summary>A nested process sheds every hosting variable; a direct one, or one with none, keeps its environment and
    /// never walks its ancestry.</summary>
    [Test]
    [Arguments(true, true, true)]
    [Arguments(true, false, false)]
    [Arguments(false, true, false)]
    public async Task A_nested_process_sheds_its_inherited_hosting(bool hosted, bool nested, bool shed) {
        var env = new Dictionary<string, string?> {
            [HostedAgent.AgentIdVar] = hosted ? "agent-1" : null, [HostedAgent.BridgeUrlVar] = hosted ? "http://127.0.0.1:1" : null,
            [HostedAgent.RenderedVar] = hosted ? HostedAgent.Rendered : null, ["PATH"] = "/usr/bin",
        };
        var walked = false;

        var result = NestedHarness.Shed(name => env.GetValueOrDefault(name), (name, value) => env[name] = value, () => { walked = true; return nested; });

        await Assert.That(result).IsEqualTo(shed);
        await Assert.That(env[HostedAgent.AgentIdVar] is null).IsEqualTo(shed || !hosted);
        await Assert.That(env[HostedAgent.BridgeUrlVar] is null).IsEqualTo(shed || !hosted);
        await Assert.That(env["PATH"]).IsEqualTo("/usr/bin");
        await Assert.That(walked).IsEqualTo(hosted);
    }
}
