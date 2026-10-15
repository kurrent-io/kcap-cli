using System.Collections.Frozen;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Acp;

namespace Capacitor.Cli.Daemon.Harness.Muse;

/// <summary>Decides every Muse approval for an unattended reviewer: only a classified-safe MCP tool is allowed, nothing else.</summary>
internal static class MuseUnattendedApprovals {
    const string NotAdmitted = "unattended reviewer: not admitted";

    /// <summary>The key a server is registered under in <c>config.mcpServers</c>. Muse names a server's
    /// tools <c>mcp__&lt;key&gt;__&lt;tool&gt;</c>, so the key is already the machine id it would otherwise derive.</summary>
    public static string ServerKey(string serverName) => serverName.Replace('-', '_');

    /// <summary>The tool names Muse shows for a launch's servers, each under the wire name
    /// <paramref name="identity"/> gave it. A canonical name is never admitted for an aliasing launch:
    /// a workspace can declare a server of that name. A server the identity did not name, or one with
    /// no unattended-safe classification, fails the launch.</summary>
    public static IReadOnlySet<string> AdmittedFor(IReadOnlyList<AcpMcpServerSpec> servers, LaunchIdentity identity) {
        if (servers.Count == 0) return FrozenSet<string>.Empty;

        var admitted = new HashSet<string>(StringComparer.Ordinal);

        foreach (var server in servers) {
            var prefix = $"mcp__{ServerKey(server.Name)}__";
            foreach (var tool in ToolsFor(server.Name, identity)) admitted.Add(prefix + tool);
        }

        return admitted.ToFrozenSet(StringComparer.Ordinal);
    }

    static IEnumerable<string> ToolsFor(string wireName, LaunchIdentity identity) {
        if (string.Equals(wireName, identity.ResultChannelWireName, StringComparison.Ordinal))
            return KcapMcpRegistry.ReservedResultChannelUnattendedSafeTools;

        if (string.Equals(wireName, identity.AllowlistWireName(KcapMcpRegistry.ReviewContextServerId), StringComparison.Ordinal))
            return [KcapMcpRegistry.ReviewContextTool];

        var canonical = KcapMcpRegistry.ReviewFlowUnattendedSafeTools.Keys
            .FirstOrDefault(id => string.Equals(identity.AllowlistWireName(id), wireName, StringComparison.Ordinal));

        return canonical is not null
            ? KcapMcpRegistry.ReviewFlowUnattendedSafeTools[canonical]
            : throw new InvalidOperationException(
                $"muse_reviewer_unclassified_server: '{wireName}' has no unattended-safe tool classification.");
    }

    public static MuseApprovalDecision Decide(JsonElement approvalParams, IReadOnlySet<string> admitted) {
        var subject = approvalParams.Prop("subject") ?? default;

        if (subject.Str("kind") == "tool"
         && subject.Str("toolName") is { } toolName
         && admitted.Contains(toolName)) {
            foreach (var choice in MuseApprovalBridge.Choices(approvalParams)) {
                if (choice.Str("choiceId") is { } id
                 && choice.Str("decision") == "approved"
                 && id == "allow_once")
                    return new MuseApprovalDecision(id, null);
            }
        }

        return new MuseApprovalDecision(MuseApprovalBridge.AbortChoiceId(MuseApprovalBridge.Choices(approvalParams)), NotAdmitted);
    }
}
