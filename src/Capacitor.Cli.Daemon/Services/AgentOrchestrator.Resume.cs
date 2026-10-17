using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Resume;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// The resume ledger: one durable entry per interactive agent, written at launch, rewritten as its
/// session ids are learned, deleted at a normal end. Flow participants never get one.
internal partial class AgentOrchestrator {
    /// The transport an entry records before its runtime exists; the rewrite from the live
    /// instance replaces it with the runtime's own.
    const string PreStartTransport = "pty";

    readonly Lock _ledgerLock = new();

    internal ResumeLedgerStore? ResumeLedger { get; }

    static bool IsInteractive(AgentInstance a) => a.Kind == LaunchKind.Default;

    internal ResumeLedgerEntry BuildLedgerEntry(
            AgentInstance agent, LaunchAgentCommand? cmd, string origin, LaunchConsentOutcome? consent) {
        var prior = TryReadLedger(agent.Id);
        var launch = new ResumeLaunchDescriptor(
            Prompt:           agent.Prompt,
            Model:            agent.Model,
            Effort:           agent.Effort,
            RepoPath:         agent.RepoPath,
            WorktreePath:     agent.Worktree.Path,
            Work:             agent.Work,
            GitRelativeCwd:   agent.Worktree.GitRelativeCwd,
            SandboxPolicy:    agent.SandboxPolicy,
            ApprovalPolicy:   agent.ApprovalPolicy,
            PermissionPreset: agent.PermissionPreset,
            McpConfigPath:    agent.McpConfigPath,
            Title:            agent.Title,
            IsPrivate:        agent.IsPrivate,
            Origin:           origin,
            RequesterUserId:  agent.RequesterUserId ?? cmd?.RequesterUserId,
            RequesterDisplay: agent.RequesterDisplay ?? cmd?.RequesterDisplay,
            ConsentSource:    consent?.Source,
            // The decision's own time: a rewrite keeps the stamp the pre-start entry took.
            ConsentAt:        consent is null ? null : prior?.Launch.ConsentAt ?? _time.GetUtcNow().ToString("O"),
            FlowRunId:        agent.FlowRunId,
            FlowRole:         agent.FlowRole);

        return NewRunningEntry(agent.Id, agent.Vendor, agent.RuntimeTransport, agent.SessionId, agent.VendorSession, launch, prior);
    }

    /// The entry an admitted server launch records before its runtime starts, from the command and
    /// the resolved worktree; no <see cref="AgentInstance"/> exists yet.
    ResumeLedgerEntry BuildPreStartLedgerEntry(
            LaunchAgentCommand cmd, string? prompt, string? model, string? effort, WorktreeInfo worktree,
            WorkLocation work, LaunchConsentOutcome consent) {
        var launch = new ResumeLaunchDescriptor(
            Prompt:           prompt,
            Model:            model,
            Effort:           effort,
            RepoPath:         cmd.RepoPath,
            WorktreePath:     worktree.Path,
            Work:             work,
            GitRelativeCwd:   worktree.GitRelativeCwd,
            SandboxPolicy:    null,
            ApprovalPolicy:   null,
            PermissionPreset: cmd.AcpPermissionPreset,
            McpConfigPath:    null,
            Title:            StartTitleOf(cmd)?.Text ?? AgentStartTitle.Shorten(prompt),
            IsPrivate:        false,
            Origin:           "server",
            RequesterUserId:  cmd.RequesterUserId,
            RequesterDisplay: cmd.RequesterDisplay,
            ConsentSource:    consent.Source,
            ConsentAt:        _time.GetUtcNow().ToString("O"),
            FlowRunId:        cmd.FlowRunId,
            FlowRole:         cmd.FlowRole);

        return NewRunningEntry(cmd.AgentId, cmd.Vendor, PreStartTransport, null, null, launch, prior: null);
    }

    ResumeLedgerEntry NewRunningEntry(
            string agentId, string vendor, string transport, string? sessionId, string? vendorSession,
            ResumeLaunchDescriptor launch, ResumeLedgerEntry? prior) {
        var now = _time.GetUtcNow();

        return new ResumeLedgerEntry(
            SchemaVersion:         ResumeLedgerEntry.CurrentSchemaVersion,
            AgentId:               agentId,
            DaemonId:              _daemonId,
            CreatedAt:             prior?.CreatedAt ?? now,
            UpdatedAt:             now,
            Vendor:                vendor,
            Transport:             transport,
            SessionId:             sessionId,
            VendorSession:         vendorSession,
            Launch:                launch,
            State:                 ResumeState.Running,
            Attempts:              0,
            LastError:             null,
            PredecessorInstanceId: null,
            ResumingAt:            null);
    }

    internal void WriteLedger(AgentInstance agent, LaunchAgentCommand? cmd, string origin, LaunchConsentOutcome? consent) {
        if (ResumeLedger is null || !IsInteractive(agent)) return;

        lock (_ledgerLock) {
            if (agent.IsCleanupStarted) return;
            TryWriteLedger(agent.Id, () => BuildLedgerEntry(agent, cmd, origin, consent));
        }
    }

    void WritePreStartLedger(
            LaunchAgentCommand cmd, string? prompt, string? model, string? effort, WorktreeInfo worktree,
            WorkLocation work, LaunchConsentOutcome consent) {
        if (ResumeLedger is null || cmd.Kind != LaunchKind.Default) return;

        lock (_ledgerLock) {
            // A live agent under this id owns the entry; a colliding launch must not overwrite it.
            if (_agents.ContainsKey(cmd.AgentId)) return;
            TryWriteLedger(cmd.AgentId, () => BuildPreStartLedgerEntry(cmd, prompt, model, effort, worktree, work, consent));
        }
    }

    internal void UpdateLedgerSession(AgentInstance agent) {
        if (ResumeLedger is null || !IsInteractive(agent)) return;

        lock (_ledgerLock) {
            if (agent.IsCleanupStarted || TryReadLedger(agent.Id) is not { } entry) return;

            TryWriteLedger(agent.Id, () => entry with {
                SessionId = agent.SessionId, VendorSession = agent.VendorSession, UpdatedAt = _time.GetUtcNow()
            });
        }
    }

    /// Removes the entry of a launch that never published an agent. An id a live agent still holds
    /// is that agent's entry, not this launch's.
    void DiscardFailedLaunchLedger(string agentId) {
        if (ResumeLedger is null || _agents.ContainsKey(agentId)) return;

        DeleteLedger(agentId);
    }

    void DeleteLedger(string agentId) {
        if (ResumeLedger is not { } ledger) return;

        lock (_ledgerLock) {
            try {
                ledger.Delete(agentId);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                LogResumeLedgerFailed(ex, "deleting", agentId);
            }
        }
    }

    void TryWriteLedger(string agentId, Func<ResumeLedgerEntry> build) {
        try {
            ResumeLedger!.Write(build());
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            LogResumeLedgerFailed(ex, "writing", agentId);
        }
    }

    ResumeLedgerEntry? TryReadLedger(string agentId) {
        try {
            return ResumeLedger?.TryRead(agentId);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            LogResumeLedgerFailed(ex, "reading", agentId);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Resume ledger: failed {Step} the entry for agent {AgentId}")]
    partial void LogResumeLedgerFailed(Exception ex, string step, string agentId);
}
