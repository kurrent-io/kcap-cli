namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// A synthetic process table (pid -> (ppid, comm)) for resolver tests. Kept out of
/// the test class so the TUnit source generator only sees [Test] methods there.
/// </summary>
static class ProcTable {
    public static Func<int, (int ppid, string comm)?> Of(params (int pid, int ppid, string comm)[] rows) =>
        pid => {
            foreach (var (p, ppid, comm) in rows) {
                if (p == pid) return (ppid, comm);
            }

            return null;
        };
}

/// <summary>
/// Tests for the pure ancestry-walk resolver behind <c>GetCodingAgentPid</c>.
/// The watcher's parent-PID watchdog only works if this resolves the durable
/// coding-agent process (claude/codex) rather than a transient hook/launcher
/// process that dies the moment the hook returns. Claude spawns its hook in a
/// separate, short-lived process group, so the old <c>getpgrp()</c> heuristic
/// resolved a dead PID and the watchdog silently never started — orphaning the
/// watcher and leaving the session stuck "active". The walk follows the ppid
/// chain (where the durable agent always appears) and matches by process name.
/// </summary>
public class CodingAgentPidResolverTests {
    [Test]
    public async Task Resolves_claude_when_it_is_the_immediate_parent() {
        // hook(100) -> claude(50) -> zsh(20) -> init(1)
        var lookup = ProcTable.Of((50, 20, "claude"), (20, 1, "-zsh"));

        var pid = ProcessHelpers.ResolveCodingAgentPid(startPid: 50, vendor: "claude", lookup);

        await Assert.That(pid).IsEqualTo(50);
    }

    [Test]
    public async Task Skips_transient_launcher_and_resolves_claude_higher_up() {
        // The real failure mode: a transient launcher/shell sits between the hook
        // and claude, so getppid() points at the launcher (which dies immediately).
        // hook(100) -> sh(90) -> claude(50) -> zsh(20)
        var lookup = ProcTable.Of((90, 50, "sh"), (50, 20, "claude"), (20, 1, "-zsh"));

        var pid = ProcessHelpers.ResolveCodingAgentPid(startPid: 90, vendor: "claude", lookup);

        await Assert.That(pid).IsEqualTo(50);
    }

    [Test]
    public async Task Resolves_codex_by_name() {
        // codex(50) -> zsh(20). The ancestry walk must work uniformly for both vendors.
        var lookup = ProcTable.Of((50, 20, "codex"), (20, 1, "-zsh"));

        var pid = ProcessHelpers.ResolveCodingAgentPid(startPid: 50, vendor: "codex", lookup);

        await Assert.That(pid).IsEqualTo(50);
    }

    [Test]
    public async Task Resolves_copilot_skipping_a_transient_launcher() {
        // Copilot is the third watcher vendor (CopilotHookCommand spawns kcap watch
        // --vendor copilot). GetCodingAgentPid now walks the ancestry for it too, so the
        // resolver must match "copilot" by name like the others.
        // hook(100) -> sh(90) -> copilot(50) -> zsh(20)
        var lookup = ProcTable.Of((90, 50, "sh"), (50, 20, "copilot"), (20, 1, "-zsh"));

        var pid = ProcessHelpers.ResolveCodingAgentPid(startPid: 90, vendor: "copilot", lookup);

        await Assert.That(pid).IsEqualTo(50);
    }

    [Test]
    public async Task Returns_nearest_agent_ancestor_when_several_match() {
        // Nested agents (a hosted agent's claude under an outer claude). The nearest
        // ancestor is the one whose death should end THIS watcher's session.
        // start(50) -> claude(50) -> sh(40) -> claude(30)
        var lookup = ProcTable.Of((50, 40, "claude"), (40, 30, "sh"), (30, 1, "claude"));

        var pid = ProcessHelpers.ResolveCodingAgentPid(startPid: 50, vendor: "claude", lookup);

        await Assert.That(pid).IsEqualTo(50);
    }

    [Test]
    public async Task Matches_agent_name_in_a_full_path_case_insensitively() {
        // comm may be a full executable path or differently cased.
        var lookup = ProcTable.Of((50, 20, "/Users/alexey/.local/bin/Claude"), (20, 1, "-zsh"));

        var pid = ProcessHelpers.ResolveCodingAgentPid(startPid: 50, vendor: "claude", lookup);

        await Assert.That(pid).IsEqualTo(50);
    }

    [Test]
    public async Task Returns_null_when_no_agent_in_chain() {
        // No claude/codex ancestor — caller falls back to the legacy heuristic.
        var lookup = ProcTable.Of((90, 20, "sh"), (20, 1, "-zsh"));

        var pid = ProcessHelpers.ResolveCodingAgentPid(startPid: 90, vendor: "claude", lookup);

        await Assert.That(pid).IsNull();
    }

    [Test]
    public async Task Stops_at_init_without_matching() {
        var lookup = ProcTable.Of((20, 1, "-zsh"));

        var pid = ProcessHelpers.ResolveCodingAgentPid(startPid: 20, vendor: "claude", lookup);

        await Assert.That(pid).IsNull();
    }

    [Test]
    public async Task Terminates_on_a_cyclic_chain_within_maxHops() {
        // Defensive: a malformed/cyclic table must not loop forever.
        var lookup = ProcTable.Of((50, 60, "sh"), (60, 50, "sh"));

        var pid = ProcessHelpers.ResolveCodingAgentPid(startPid: 50, vendor: "claude", lookup, maxHops: 8);

        await Assert.That(pid).IsNull();
    }

    [Test]
    public async Task Returns_null_when_start_pid_is_not_inspectable() {
        var lookup = ProcTable.Of(); // every lookup returns null

        var pid = ProcessHelpers.ResolveCodingAgentPid(startPid: 50, vendor: "claude", lookup);

        await Assert.That(pid).IsNull();
    }

    [Test]
    public async Task Does_not_match_unrelated_substring_processes() {
        // "Claude Helper (Renderer)" is the Electron desktop app, not the CLI: a
        // basename match (not a loose substring) must reject it.
        var lookup = ProcTable.Of((50, 20, "Claude Helper (Renderer)"), (20, 1, "-zsh"));

        var pid = ProcessHelpers.ResolveCodingAgentPid(startPid: 50, vendor: "claude", lookup);

        await Assert.That(pid).IsNull();
    }
}
