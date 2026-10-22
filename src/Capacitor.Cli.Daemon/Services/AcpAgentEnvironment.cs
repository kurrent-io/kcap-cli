using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Ties an ACP child to the agent and the daemon hosting it. The session the server files an
/// ACP agent under exists only after <c>session/new</c>, once this environment is fixed, so kcap's MCP
/// servers inside the child ask the daemon named here for the session of the agent named here.</summary>
internal static class AcpAgentEnvironment {
    /// <summary>Another harness's own-session ids, inherited from the shell the daemon started in. Left in
    /// place they name that shell's session, which kcap's MCP servers would then act for.</summary>
    internal static readonly string[] InheritedSessionVars = ["CLAUDE_CODE_SESSION_ID", "KCAP_SESSION_ID", "CODEX_THREAD_ID"];

    /// <summary>For an environment seeded from the daemon's own. A private agent carries no agent id, as
    /// a PTY launch's does not: nothing may tie its session to the agent.</summary>
    internal static void Apply(IDictionary<string, string?> env, RuntimeStartContext ctx, string daemonName) {
        foreach (var name in InheritedSessionVars) env.Remove(name);

        if (ctx.IsPrivate) env.Remove(HostedAgent.AgentIdVar);
        else env[HostedAgent.AgentIdVar] = ctx.AgentId;

        if (daemonName.Length > 0)                  env[DaemonNameResolver.NameVar] = daemonName;
        if (!string.IsNullOrEmpty(ctx.DaemonId))    env["KCAP_DAEMON_ID"]           = ctx.DaemonId;
        if (!string.IsNullOrEmpty(ctx.DaemonEpoch)) env["KCAP_DAEMON_EPOCH"]        = ctx.DaemonEpoch;
    }
}
