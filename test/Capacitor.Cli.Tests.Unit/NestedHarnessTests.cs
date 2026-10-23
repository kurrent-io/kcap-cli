namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// A hook links its session to the hosted agent only when no other coding agent sits between it and the daemon: a
/// <c>codex exec</c> a hosted Claude runs inherits <c>KCAP_AGENT_ID</c> but its session is its own.
/// </summary>
public class NestedHarnessTests {
    static readonly IReadOnlyList<string> Vendors = ["claude", "codex"];

    [Test]
    // hook's parent sh(90) -> claude(50) -> kcap daemon(10)
    [Arguments(false, new[] { 90, 50, 10 }, new[] { "sh", "claude", "kcap" })]
    // codex(80) -> zsh(70) -> claude(50) -> kcap(10): a nested agent
    [Arguments(true, new[] { 80, 70, 50, 10 }, new[] { "codex", "zsh", "claude", "kcap" })]
    // codex npm shim(81) -> codex binary(80) -> kcap(10): one agent, launched through its own shim
    [Arguments(false, new[] { 81, 80, 10 }, new[] { "codex", "codex", "kcap" })]
    // claude(50) -> kcap(10) -> zsh(5) -> codex(3): a daemon started inside another agent's session
    [Arguments(false, new[] { 50, 10, 5, 3 }, new[] { "claude", "kcap", "zsh", "codex" })]
    // codex(80) -> zsh(70) -> claude(50), then unreadable: what was seen still counts
    [Arguments(true, new[] { 80, 70, 50 }, new[] { "codex", "zsh", "claude" })]
    public async Task A_second_coding_agent_below_the_daemon_marks_the_hook_nested(bool nested, int[] pids, string[] names) {
        var rows = pids.Select((pid, i) => (pid, ppid: i + 1 < pids.Length ? pids[i + 1] : 999, names[i])).ToArray();

        await Assert.That(NestedHarness.IsNested(pids[0], Vendors, ProcTable.Of(rows))).IsEqualTo(nested);
    }
}
