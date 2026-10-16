using System.Text.Json.Nodes;

namespace Capacitor.Cli.Daemon.Harness.Muse;

/// <summary>How one launch drives its host: the lane's posture, the session to start or resume, and the
/// tool names an unattended reviewer may approve.</summary>
internal sealed record MuseLaunchPlan(
    bool                 IsReviewer,
    string               PermissionProfile,
    string               ApprovalMode,
    JsonObject?          McpServers,
    string?              ResumeSessionId,
    IReadOnlySet<string> AdmittedTools,
    string               MintedSessionId
);
