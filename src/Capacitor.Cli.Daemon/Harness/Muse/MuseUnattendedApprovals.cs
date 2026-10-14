using System.Collections.Frozen;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;

namespace Capacitor.Cli.Daemon.Harness.Muse;

/// <summary>Decides every Muse approval for an unattended reviewer: only a classified-safe MCP tool is allowed, nothing else.</summary>
internal static class MuseUnattendedApprovals {
    const string NotAdmitted = "unattended reviewer: not admitted";

    /// <summary>The tool names Muse shows for a launch's servers: <c>mcp__&lt;server, '-' as '_'&gt;__&lt;tool&gt;</c>.
    /// A server with no unattended-safe classification fails the launch.</summary>
    public static IReadOnlySet<string> AdmittedFor(IReadOnlyList<AcpMcpServerSpec> servers) {
        if (servers.Count == 0) return FrozenSet<string>.Empty;

        var admitted = new HashSet<string>(StringComparer.Ordinal);

        foreach (var server in servers) {
            IReadOnlySet<string> tools;

            if (KcapMcpRegistry.IsReservedResultChannel(server.Name))
                tools = KcapMcpRegistry.ReservedResultChannelUnattendedSafeTools;
            else if (!KcapMcpRegistry.ReviewFlowUnattendedSafeTools.TryGetValue(server.Name, out tools!))
                throw new InvalidOperationException(
                    $"muse_reviewer_unclassified_server: '{server.Name}' has no unattended-safe tool classification.");

            var prefix = $"mcp__{server.Name.Replace('-', '_')}__";
            foreach (var tool in tools) admitted.Add(prefix + tool);
        }

        return admitted.ToFrozenSet(StringComparer.Ordinal);
    }

    public static MuseApprovalDecision Decide(JsonElement approvalParams, IReadOnlySet<string> admitted) {
        var subject = approvalParams.ValueKind == JsonValueKind.Object && approvalParams.TryGetProperty("subject", out var s) ? s : default;

        if (MuseApprovalBridge.StringOf(subject, "kind") == "tool"
         && MuseApprovalBridge.StringOf(subject, "toolName") is { } toolName
         && admitted.Contains(toolName)) {
            foreach (var choice in MuseApprovalBridge.Choices(approvalParams)) {
                if (MuseApprovalBridge.StringOf(choice, "choiceId") is { } id
                 && MuseApprovalBridge.StringOf(choice, "decision") == "approved"
                 && id == "allow_once")
                    return new MuseApprovalDecision(id, null);
            }
        }

        return new MuseApprovalDecision(MuseApprovalBridge.AbortChoiceId(MuseApprovalBridge.Choices(approvalParams)), NotAdmitted);
    }
}
