namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// A hook links its session to the hosted agent only when no other coding agent sits between it and the daemon: a
/// <c>codex exec</c> a hosted Claude runs inherits <c>KCAP_AGENT_ID</c> but its session is its own. Each argument row is
/// the ancestry from the hook's parent upward, one process per entry, its names separated by '|'.
/// </summary>
public class NestedHarnessTests {
    static readonly IReadOnlyList<string> Vendors = ["claude", "codex", "gemini"];

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
    // gemini -> gemini -> kcap: an agent that runs under node, started directly by another
    [Arguments(true, new[] { "node|gemini", "node|gemini", "kcap" })]
    // claude -> kcap-daemon -> zsh -> codex: a daemon started inside another agent's session
    [Arguments(false, new[] { "claude", "kcap-daemon", "zsh", "codex" })]
    // codex -> zsh -> claude, then unreadable: what was seen still counts
    [Arguments(true, new[] { "codex", "zsh", "claude" })]
    public async Task A_second_coding_agent_below_the_daemon_marks_the_hook_nested(bool nested, string[] ancestry) {
        var rows = ancestry.Select((names, i) => (pid: 100 - i, ppid: i + 1 < ancestry.Length ? 99 - i : 2, names.Split('|'))).ToArray();

        await Assert.That(NestedHarness.IsNested(100, Vendors, ProcTable.OfNames(rows))).IsEqualTo(nested);
    }
}
