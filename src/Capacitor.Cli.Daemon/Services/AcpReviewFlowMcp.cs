using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Builds the <see cref="AcpMcpServerSpec"/> list a review-flow reviewer's <c>session/new</c> must
/// carry — the ACP-layer analogue of <see cref="ClaudeLauncher"/>'s PTY <c>BuildReviewFlowMcpConfig</c>,
/// reproducing it exactly: the <c>kcap-flow-result</c> submission channel (the ONLY way a reviewer
/// returns its verdict) plus the flow definition's MCP allowlist resolved against the kcap-owned
/// <see cref="KcapMcpRegistry"/> (never ambient user config), with flow-starting servers stripped
/// (the recursion guard).
///
/// Called ONLY after <see cref="AcpHostedAgentRuntimeFactory"/>'s pre-spawn validation has confirmed
/// <see cref="RuntimeStartContext.ServerUrl"/>, <see cref="RuntimeStartContext.CapacitorPath"/>, and
/// <see cref="RuntimeStartContext.AgentId"/> are all non-blank, so this builder never emits a server
/// with a blank command or a dead result channel.
///
/// Reads the launch's own <c>ctx</c> fields (not <c>DaemonConfig</c>) so there is a single source of
/// truth with the rest of the ACP factory, which already stamps <c>KCAP_URL</c> from
/// <see cref="RuntimeStartContext.ServerUrl"/>.
/// </summary>
internal static class AcpReviewFlowMcp {
    internal static IReadOnlyList<AcpMcpServerSpec> Build(RuntimeStartContext ctx) {
        var servers = new List<AcpMcpServerSpec> {
            // The result channel. Both env vars are mandatory — the flow-result MCP server
            // Environment.Exit(2)s when KCAP_FLOW_AGENT_ID is absent.
            new("kcap-flow-result", ctx.CapacitorPath, ["mcp", "flow-result"],
                [new("KCAP_URL", ctx.ServerUrl!), new("KCAP_FLOW_AGENT_ID", ctx.AgentId)])
        };

        // Dedup by canonical id (case-insensitive): ClaudeLauncher writes into a JsonObject keyed
        // by descriptor.Id, so ["kcap-sessions","KCAP-SESSIONS"] collapses to one server there. A
        // list-append helper must match, or duplicate AcpMcpServerSpec.Name entries would make the
        // session/new payload's behavior undefined (a vendor may reject it or pick arbitrarily).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in ctx.McpAllowlist ?? []) {
            var descriptor = KcapMcpRegistry.Resolve(name);

            // Skip unknown (unresolvable) names and flow-starting servers (recursion guard — a
            // reviewer can never be handed kcap-flows, incl. case-variants the registry canonicalizes).
            if (descriptor is null || descriptor.StartsFlows) continue;
            if (!seen.Add(descriptor.Id)) continue;

            // Allowlist servers get KCAP_URL only — never KCAP_FLOW_AGENT_ID, which is exclusive to
            // the result channel.
            servers.Add(new(descriptor.Id, ctx.CapacitorPath, descriptor.Args,
                [new("KCAP_URL", ctx.ServerUrl!)]));
        }

        return servers;
    }
}
