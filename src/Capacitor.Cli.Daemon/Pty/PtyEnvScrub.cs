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

    public static readonly string[] DaemonSupervisionVars = [
        "KCAP_DAEMON_SUPERVISED",
        "XPC_SERVICE_NAME",
        "INVOCATION_ID",
        "SYSTEMD_EXEC_PID"
    ];

    public static readonly string[] HostedAgentVars = [
        "KCAP_AGENT_ID",
        "KCAP_RENDERED_AGENT",
        "KCAP_DAEMON_URL"
    ];
}
