namespace Capacitor.Cli.Daemon.Services;

/// <summary>Another harness's own-session ids, inherited from the shell the daemon started in. A child
/// environment seeded from the daemon's would carry them, and left in place they name that shell's
/// session, which kcap's MCP servers inside the child would then act for. A harness that names its own
/// session sets these for its children itself.</summary>
internal static class InheritedHarnessSessions {
    internal static readonly string[] Vars = ["CLAUDE_CODE_SESSION_ID", "KCAP_SESSION_ID", "CODEX_THREAD_ID"];

    internal static void Remove(IDictionary<string, string?> env) {
        foreach (var name in Vars) env.Remove(name);
    }
}
