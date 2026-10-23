using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli;

/// <summary>
/// Whether this process runs under a coding agent that another coding agent started, such as a <c>codex exec</c> a
/// hosted Claude runs. The nested agent inherits the hosted agent's environment, <c>KCAP_AGENT_ID</c> included, but its
/// sessions are its own, so it must not link them to the hosted agent.
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

    static readonly Lazy<bool> CurrentProcess = new(() =>
        ProcessHelpers.GetParentPid() is { } parent
     && IsNested(parent, HarnessRegistry.Identities.Select(h => h.VendorId).ToList(), ProcessHelpers.GetProcessInfo));

    /// <summary>The hosted agent's id when a hook may link this process's session to it.</summary>
    public static string? LinkedAgentId(this HostedAgent hosted) => hosted.AgentId is { } id && !CurrentProcess.Value ? id : null;

    /// <summary>Walks the ancestry from <paramref name="startPid"/> to the daemon, or as far as it can be read, and
    /// reports whether it holds more than one coding agent. An unreadable ancestry reports what it saw.</summary>
    internal static bool IsNested(
            int                                                 startPid,
            IReadOnlyList<string>                               vendors,
            Func<int, (int ppid, IReadOnlyList<string> names)?> lookup,
            int                                                 maxHops = 32
        ) {
        var     agents     = 0;
        var     pastSelf   = false;
        string? childAgent       = null;
        var     childInterpreted = false;
        var     pid        = startPid;

        for (var hop = 0; hop < maxHops && pid > 1; hop++) {
            if (lookup(pid) is not { } info) break;

            if (Is(info.names, Kcap)) {
                if (pastSelf) break;

                pid = info.ppid;
                continue;
            }

            pastSelf = true;

            var agent       = vendors.FirstOrDefault(vendor => Is(info.names, [vendor]));
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
