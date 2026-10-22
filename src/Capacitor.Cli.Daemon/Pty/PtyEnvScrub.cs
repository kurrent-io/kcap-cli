using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
namespace Capacitor.Cli.Daemon.Pty;

internal static class PtyEnvScrub {
    public static readonly string[] ClaudeSessionVars = [
        "CLAUDECODE",
        "CLAUDE_CODE_ENTRYPOINT",
        "ANTHROPIC_API_KEY",
        "CLAUDE_CODE_CHILD_SESSION",
        "CLAUDE_CODE_SESSION_ID",
        "CLAUDE_ENV_FILE",
        "ANTHROPIC_API_KEY_CLAUDE_CODE_BACKUP"
    ];

    /// <summary>Another harness's own-session ids. A hosted Claude or Codex sets its own for its children,
    /// and no other vendor sets any, so an inherited one would name the daemon's shell's session.</summary>
    public static IReadOnlyList<string> InheritedSessionVars => InheritedHarnessSessions.Vars;

    public static readonly string[] DaemonSupervisionVars = [
        "KCAP_DAEMON_SUPERVISED",
        "XPC_SERVICE_NAME",
        "INVOCATION_ID",
        "SYSTEMD_EXEC_PID"
    ];

    public static readonly string[] HostedAgentVars = [
        HostedAgent.AgentIdVar,
        HostedAgent.RenderedVar,
        HostedAgent.BridgeUrlVar,
        HostedAgent.SessionKindVar,
        HostedAgent.FlowRoleVar,
        HostedAgent.DaemonAgentIdVar
    ];
}
