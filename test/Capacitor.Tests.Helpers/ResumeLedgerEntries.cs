using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Resume;

namespace Capacitor.Tests.Helpers;

public static class ResumeLedgerEntries {
    public static ResumeLedgerEntry For(string agentId, ResumeState state, string daemonId = "daemon-1") {
        var stamp = DateTimeOffset.UnixEpoch;
        var launch = new ResumeLaunchDescriptor(
            Prompt: null, Model: null, Effort: null, RepoPath: "/repo", WorktreePath: "/repo/wt",
            Work: WorkLocation.OwnedWorktree, GitRelativeCwd: null, SandboxPolicy: null, ApprovalPolicy: null,
            PermissionPreset: null, McpConfigPath: null, Title: null, IsPrivate: false, Origin: "local",
            RequesterUserId: null, RequesterDisplay: null, ConsentSource: null, ConsentAt: null,
            FlowRunId: null, FlowRole: null);

        return new ResumeLedgerEntry(
            ResumeLedgerEntry.CurrentSchemaVersion, agentId, daemonId, stamp, stamp, "claude", "pty",
            null, null, launch, state, 0, null, PredecessorInstanceId: null, ResumingAt: null);
    }
}
