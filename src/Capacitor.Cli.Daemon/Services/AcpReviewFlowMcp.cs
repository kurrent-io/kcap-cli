using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Builds the review-flow reviewer's <c>session/new</c> <see cref="AcpMcpServerSpec"/> list — the
/// ACP analogue of <see cref="ClaudeLauncher"/>'s PTY <c>BuildReviewFlowMcpConfig</c>: the
/// <c>kcap-flow-result</c> submit channel plus the reviewer allowlist.
///
/// Caller (<see cref="AcpHostedAgentRuntimeFactory"/>) supplies <paramref name="allowlistServerIds"/>
/// already resolved and validated via <see cref="KcapMcpRegistry.TryResolveReviewFlowAllowlist"/>,
/// so every id here is a canonical, auto-approvable read-only server. Reads the launch's own
/// <c>ctx</c> fields (validated non-blank) rather than <c>DaemonConfig</c>, matching the factory.
/// </summary>
internal static class AcpReviewFlowMcp {
    /// <summary>The reserved result-channel server id. Always injected here, so it is NOT a
    /// <see cref="KcapMcpRegistry"/> entry — callers drop it from the reviewer allowlist as a no-op
    /// before validating the rest (the server's dynamic-flow policy may list it explicitly).</summary>
    internal const string ResultChannelId = "kcap-flow-result";

    internal static IReadOnlyList<AcpMcpServerSpec> Build(RuntimeStartContext ctx, IReadOnlyList<string> allowlistServerIds) {
        // The result channel: both env vars are mandatory (the flow-result MCP server exits when
        // KCAP_FLOW_AGENT_ID is absent). KCAP_FLOW_AGENT_ID is exclusive to it.
        var servers = new List<AcpMcpServerSpec> {
            new(ResultChannelId, ctx.CapacitorPath, ["mcp", "flow-result"],
                [new("KCAP_URL", ctx.ServerUrl!), new("KCAP_FLOW_AGENT_ID", ctx.AgentId)])
        };

        foreach (var id in allowlistServerIds) {
            // id is a validated canonical id, so Resolve is non-null. Allowlist servers get KCAP_URL only.
            var descriptor = KcapMcpRegistry.Resolve(id)!;
            servers.Add(new(descriptor.Id, ctx.CapacitorPath, descriptor.Args, [new("KCAP_URL", ctx.ServerUrl!)]));
        }

        return servers;
    }
}
