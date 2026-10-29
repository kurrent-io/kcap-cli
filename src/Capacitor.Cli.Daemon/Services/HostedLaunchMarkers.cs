using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Stamps <see cref="HostedAgent.LaunchMarkers"/> into a launched agent's environment, which the
/// hook commands it spawns read to decide what SessionStart context the session is given, and
/// <see cref="HostedAgent.DaemonLookupMarkers"/>, by which its kcap MCP servers find its session. A
/// private agent carries no lookup markers: nothing may tie its session to the agent.</summary>
internal static class HostedLaunchMarkers {
    /// <summary>For an environment seeded from the daemon's own: a role, agent or session inherited from it
    /// would outlive a launch that names none.</summary>
    public static void Apply(IDictionary<string, string?> env, RuntimeStartContext ctx) {
        env.Remove(HostedAgent.FlowRoleVar);
        env.Remove(HostedAgent.DaemonAgentIdVar);
        InheritedHarnessSessions.Remove(env);
        foreach (var (key, value) in Markers(ctx)) env[key] = value;
    }

    /// <summary>For an overlay applied over a scrubbed environment (the PTY spawn's).</summary>
    public static void ApplyOverlay(IDictionary<string, string> env, RuntimeStartContext ctx) {
        foreach (var (key, value) in Markers(ctx)) env[key] = value;
    }

    /// <summary>For a local spawn, which has no launch context.</summary>
    public static void ApplyLocal(IDictionary<string, string> env, string agentId, string daemonName) {
        foreach (var (key, value) in HostedAgent.LaunchMarkers(isFlowParticipant: false, flowRole: null)) env[key] = value;
        foreach (var (key, value) in HostedAgent.DaemonLookupMarkers(agentId, daemonName)) env[key] = value;
    }

    static IEnumerable<KeyValuePair<string, string>> Markers(RuntimeStartContext ctx) =>
        ctx.IsPrivate
            ? HostedAgent.LaunchMarkers(ctx.IsReviewFlow, ctx.FlowRole)
            : [.. HostedAgent.LaunchMarkers(ctx.IsReviewFlow, ctx.FlowRole), .. HostedAgent.DaemonLookupMarkers(ctx.AgentId, ctx.DaemonName)];
}
