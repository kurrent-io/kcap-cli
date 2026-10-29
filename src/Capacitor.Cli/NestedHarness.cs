using Capacitor.Cli.Core;

namespace Capacitor.Cli;

/// <summary>
/// Whether this process runs under a coding agent that another coding agent started, such as a <c>codex exec</c> a
/// hosted Claude runs. The nested agent inherits the hosted agent's environment, <c>KCAP_AGENT_ID</c> included, but its
/// sessions are its own: it must not link them to the hosted agent, route its approvals through the hosted agent's
/// daemon bridge, or have its session end held back by the hosted agent's resume.
/// </summary>
public static class NestedHarness {
    /// <summary>The CLI's own executables, the daemon's among them. Those this process was launched through (the npm
    /// launcher runs the native CLI as its child) are skipped; the next one up is the daemon, where the walk stops, so a
    /// daemon started from inside an agent's session does not make the agents it hosts look nested.</summary>
    static readonly string[] Kcap = ["kcap", "kcap-daemon"];

    /// <summary>Interpreters a launcher script runs under.</summary>
    static readonly string[] Interpreters = ["node", "bun", "deno", "python", "python3"];

    /// <summary>Vendors whose interpreted process is only ever a launcher for their native agent: Codex's npm package
    /// runs a script that starts the native binary. Such a launcher counts as one agent with the native agent it
    /// started. Other vendors' interpreted processes can be agents themselves (Claude and Gemini run under Node), so a
    /// same-vendor parent of theirs is a second agent.</summary>
    static readonly string[] LauncherVendors = ["codex"];

    /// <summary>The process names of the coding agents.</summary>
    internal static readonly string[] AgentNames = [..ProcessHelpers.NestableAgents, "muse"];

    /// <summary>What a daemon puts into a hosted agent's environment to name it as the host.</summary>
    static readonly string[] HostingVars = [
        HostedAgent.AgentIdVar, HostedAgent.DaemonAgentIdVar, HostedAgent.RenderedVar, HostedAgent.BridgeUrlVar,
        HostedAgent.SessionKindVar, HostedAgent.FlowRoleVar,
    ];

    /// <summary>Removes the inherited hosting variables from this process's environment when it is nested, so it reads
    /// as a terminal session and so does every process it starts, a detached watcher included. Runs before anything
    /// reads them.</summary>
    public static void ShedInheritedHosting() =>
        Shed(Environment.GetEnvironmentVariable, Environment.SetEnvironmentVariable,
             () => ProcessHelpers.GetParentPid() is { } parent && IsNested(parent, AgentNames, ProcessHelpers.GetProcessInfo));

    /// <summary>Walks the ancestry only when a hosting variable is present.</summary>
    internal static bool Shed(Func<string, string?> get, Action<string, string?> set, Func<bool> nested) {
        if (HostingVars.All(name => string.IsNullOrEmpty(get(name))) || !nested()) return false;

        foreach (var name in HostingVars) set(name, null);

        return true;
    }

    /// <summary>Walks the ancestry from <paramref name="startPid"/> to the daemon, or as far as it can be read, and
    /// reports whether it holds more than one coding agent; <paramref name="agentNames"/> are the agents' process names. An
    /// unreadable or cyclic ancestry reports what it saw.</summary>
    internal static bool IsNested(
            int                                                 startPid,
            IReadOnlyList<string>                               agentNames,
            Func<int, (int ppid, IReadOnlyList<string> names)?> lookup
        ) {
        var     visited    = new HashSet<int>();
        var     agents     = 0;
        var     pastSelf   = false;
        string? childAgent       = null;
        var     childInterpreted = false;
        var     pid        = startPid;

        while (pid > 1 && visited.Add(pid)) {
            if (lookup(pid) is not { } info) break;

            if (Is(info.names, Kcap)) {
                if (pastSelf) break;

                pid = info.ppid;
                continue;
            }

            pastSelf = true;

            var agent       = agentNames.FirstOrDefault(name => Is(info.names, [name]));
            var interpreted = Is(info.names, Interpreters);
            var launcher    = agent is not null && agent == childAgent && LauncherVendors.Contains(agent) && interpreted && !childInterpreted;
            if (agent is not null && !launcher && ++agents > 1) return true;

            childAgent       = agent;
            childInterpreted = interpreted;
            pid              = info.ppid;
        }

        return false;
    }

    static bool Is(IReadOnlyList<string> names, IReadOnlyList<string> executables) =>
        names.Any(name => executables.Any(executable => ProcessHelpers.MatchesAgentName(name, executable)));
}
