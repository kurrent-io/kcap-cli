using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Pty.Unix;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Pty;

public class PtyEnvScrubTests {
    [Test]
    public async Task Claude_session_vars_are_shared_by_pty_implementations() {
        await Assert.That(PtyEnvScrub.ClaudeSessionVars).IsEquivalentTo(new[] {
            "CLAUDECODE",
            "CLAUDE_CODE_ENTRYPOINT",
            "ANTHROPIC_API_KEY",
            "CLAUDE_CODE_CHILD_SESSION",
            "CLAUDE_CODE_SESSION_ID",
            "CLAUDE_ENV_FILE",
            "ANTHROPIC_API_KEY_CLAUDE_CODE_BACKUP"
        }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Daemon_supervision_vars_are_shared_by_pty_implementations() {
        await Assert.That(PtyEnvScrub.DaemonSupervisionVars).IsEquivalentTo(new[] {
            "KCAP_DAEMON_SUPERVISED",
            "XPC_SERVICE_NAME",
            "INVOCATION_ID",
            "SYSTEMD_EXEC_PID"
        }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Hosted_agent_vars_are_shared_by_pty_implementations() {
        await Assert.That(PtyEnvScrub.HostedAgentVars).IsEquivalentTo(new[] {
            "KCAP_AGENT_ID",
            "KCAP_RENDERED_AGENT",
            "KCAP_DAEMON_URL",
            "KCAP_SESSION_KIND",
            "KCAP_FLOW_ROLE",
            "KCAP_DAEMON_AGENT_ID"
        }, CollectionOrdering.Matching);
    }

    /// <summary>A PTY vendor other than Claude or Codex sets none of these for its children, so a value
    /// inherited from the daemon's shell would name that shell's session to the child's MCP servers. The
    /// values are seeded here, so a child without them is the scrub's own doing; the overlay names only
    /// the lookup, as a hosted launch's does.</summary>
    [Test, NotInParallel]
    [Arguments("CLAUDE_CODE_SESSION_ID")]
    [Arguments("KCAP_SESSION_ID")]
    [Arguments("CODEX_THREAD_ID")]
    public async Task A_pty_child_does_not_inherit_another_harnesss_session(string name) {
        using var inherited = EnvScope.Exclusive(name, "inherited-from-the-daemons-shell");

        var env = UnixPtyProcess.BuildChildEnv(new() { ["KCAP_DAEMON_AGENT_ID"] = "agent-1" }, removeEnv: null, cols: 80, rows: 24);

        await Assert.That(env.TryGetValue(name, out var value) ? value : null).IsNotEqualTo("inherited-from-the-daemons-shell");
        await Assert.That(env["KCAP_DAEMON_AGENT_ID"]).IsEqualTo("agent-1");
    }
}
