using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli;

/// <summary>
/// Whether this process runs under a coding agent that another coding agent started, such as a <c>codex exec</c> a
/// hosted Claude runs. The nested agent inherits the hosted agent's environment, <c>KCAP_AGENT_ID</c> included, but its
/// sessions are its own, so it must not link them to the hosted agent.
/// </summary>
public static class NestedHarness {
    /// <summary>The daemon's executables, as the CLI and as its own project. The walk stops there, so a daemon started
    /// from inside an agent's session does not make the agents it hosts look nested.</summary>
    static readonly string[] Daemons = ["kcap", "kcap-daemon"];

    static readonly Lazy<bool> CurrentProcess = new(() =>
        ProcessHelpers.GetParentPid() is { } parent
     && IsNested(parent, HarnessRegistry.Identities.Select(h => h.VendorId).ToList(), ProcessHelpers.GetProcessInfo));

    /// <summary>The hosted agent's id when a hook may link this process's session to it.</summary>
    public static string? LinkedAgentId(this HostedAgent hosted) => hosted.AgentId is { } id && !CurrentProcess.Value ? id : null;

    /// <summary>Walks the ancestry from <paramref name="startPid"/> to the daemon, or as far as it can be read, and
    /// reports whether it holds more than one coding agent. A process of the same vendor as its child counts with it,
    /// since a vendor's launcher can exec its own binary (Codex's npm shim does). An unreadable ancestry reports what it
    /// saw.</summary>
    internal static bool IsNested(
            int                                                 startPid,
            IReadOnlyList<string>                               vendors,
            Func<int, (int ppid, IReadOnlyList<string> names)?> lookup,
            int                                                 maxHops = 32
        ) {
        var     agents     = 0;
        string? childAgent = null;
        var     pid        = startPid;

        for (var hop = 0; hop < maxHops && pid > 1; hop++) {
            if (lookup(pid) is not { } info) break;

            if (info.names.Any(name => Daemons.Any(daemon => ProcessHelpers.MatchesAgentName(name, daemon)))) break;

            var agent = vendors.FirstOrDefault(vendor => info.names.Any(name => ProcessHelpers.MatchesAgentName(name, vendor)));
            if (agent is not null && agent != childAgent && ++agents > 1) return true;

            childAgent = agent;
            pid        = info.ppid;
        }

        return false;
    }
}
