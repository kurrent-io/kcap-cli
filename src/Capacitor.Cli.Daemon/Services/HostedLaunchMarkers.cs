using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Stamps <see cref="HostedAgent.LaunchMarkers"/> into a launched agent's environment. The
/// hook commands it spawns read them to decide what SessionStart context the session is given.</summary>
internal static class HostedLaunchMarkers {
    /// <summary>For an environment seeded from the daemon's own: a role inherited from it would
    /// outlive a launch that names none.</summary>
    public static void Apply(IDictionary<string, string?> env, RuntimeStartContext ctx) {
        env.Remove(HostedAgent.FlowRoleVar);
        foreach (var (key, value) in HostedAgent.LaunchMarkers(ctx.IsReviewFlow, ctx.FlowRole)) env[key] = value;
    }

    /// <summary>For an overlay applied over a scrubbed environment (the PTY spawn's).</summary>
    public static void Apply(IDictionary<string, string> env, bool isFlowParticipant, string? flowRole) {
        foreach (var (key, value) in HostedAgent.LaunchMarkers(isFlowParticipant, flowRole)) env[key] = value;
    }
}
