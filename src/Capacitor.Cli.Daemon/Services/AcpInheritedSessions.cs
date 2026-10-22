namespace Capacitor.Cli.Daemon.Services;

/// <summary>Another harness's own-session ids, inherited from the shell the daemon started in. An ACP
/// child's environment is seeded from the daemon's, and left in place these name that shell's session,
/// which kcap's MCP servers inside the child would then act for.</summary>
internal static class AcpInheritedSessions {
    internal static readonly string[] Vars = ["CLAUDE_CODE_SESSION_ID", "KCAP_SESSION_ID", "CODEX_THREAD_ID"];

    internal static void Remove(IDictionary<string, string?> env) {
        foreach (var name in Vars) env.Remove(name);
    }
}
