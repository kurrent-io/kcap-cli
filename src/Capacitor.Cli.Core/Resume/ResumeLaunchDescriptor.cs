using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Core.Resume;

public sealed record ResumeLaunchDescriptor(
    string? Prompt,
    string? Model,
    string? Effort,
    string RepoPath,
    string WorktreePath,
    WorkLocation Work,
    string? GitRelativeCwd,
    string? SandboxPolicy,
    string? ApprovalPolicy,
    string? PermissionPreset,
    string? McpConfigPath,
    string? Title,
    bool IsPrivate,
    string Origin,
    string? RequesterUserId,
    string? RequesterDisplay,
    string? ConsentSource,
    string? ConsentAt,
    string? FlowRunId,
    string? FlowRole,
    string? WorktreeBranch = null,
    bool? WorktreeIsStandalone = null,
    string? WorktreeSourceRepo = null,
    string? PermissionMode = null,
    // A ledger entry without the field reads as null, and resume treats it as derived.
    bool? TitleDerived = null,
    int? InactivityBoundSeconds = null,
    string? AccountDirectory = null,
    string? AccountId = null,
    string? AccountLabel = null
);
