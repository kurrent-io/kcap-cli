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

    internal static readonly byte[] RestartMarker = "\r\n── kcap daemon restarted; session resumed ──\r\n"u8.ToArray();

    internal ShutdownIntent ShutdownIntent { get; set; } = ShutdownIntent.KeepAgents;

    /// Slices a replayed ring so the buffer evicts it gradually rather than whole on the next chunk.
    const int RingReplaySlice = 64 * 1024;

    const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// Stops an interactive agent so a successor daemon can resume it, leaving its ledger entry
    /// <see cref="ResumeState.Resuming"/> and its terminal ring on disk. An agent with no readable
    /// entry has nothing to resume from and is left to the ordinary shutdown.
    internal async Task PrepareForResumeAsync(AgentInstance agent, CancellationToken ct) {
        if (!TryMarkResuming(agent)) return;

        byte[] ring;

        lock (agent.SinksLock) {
            foreach (var sink in agent.LocalSinks) (sink as LocalSocketSink)?.Complete();
            agent.LocalSinks.Clear();
            ring = agent.OutputBuffer.Snapshot();
        }

        if (agent.CloudSink is { } cloud) {
            try {
                await cloud.StopAsync(TimeSpan.Zero);
            } catch (Exception ex) {
                LogCloudSinkStopFailed(ex, agent.Id);
            }
        }

        TryWriteRing(agent.Id, ring);

        var graceful = Task.CompletedTask;

        // The vendor saves its session on a graceful exit; a resume reopens what it saved.
        try {
            graceful = agent.Runtime.RequestGracefulStopAsync();
            await graceful.WaitAsync(GracefulExitWait, _time, ct);
            await agent.Runtime.WaitForExitAsync(GracefulExitWait);
        } catch (Exception ex) {
            if (!graceful.IsCompleted) ObserveAbandonedGracefulStop(graceful, agent.Id);
            LogGracefulExitFailed(ex, agent.Id);
        }

        await agent.Runtime.TerminateAsync(TimeSpan.FromSeconds(5));
    }

    /// Appends a predecessor's ring, at most <see cref="TerminalOutputBuffer.MaxBytes"/> of its tail,
    /// then <see cref="RestartMarker"/>.
    internal static void ReplayRing(AgentInstance agent, byte[] ring) {
        var tail = ring.AsSpan(Math.Max(0, ring.Length - TerminalOutputBuffer.MaxBytes));

        for (var offset = 0; offset < tail.Length; offset += RingReplaySlice)
            agent.OutputBuffer.Append(tail.Slice(offset, Math.Min(RingReplaySlice, tail.Length - offset)).ToArray());

        agent.OutputBuffer.Append(RestartMarker);
    }

    /// The per-agent shutdown step. Under <see cref="ShutdownIntent.KeepAgents"/> interactive agents
    /// stop for resume and resumable reviewers park; everything else is terminated.
    async Task StopAgentsForShutdownAsync(IReadOnlyList<AgentInstance> live) {
        if (ShutdownIntent == ShutdownIntent.KeepAgents) {
            await Task.WhenAll(live.Where(IsInteractive).Select(PrepareForShutdownResumeAsync));
            await ParkReviewersForShutdownAsync([.. live.Where(a => !IsInteractive(a) && IsResumableReviewer(a))]);
        }

        foreach (var agent in live.Where(a => !a.ResumePending)) {
            try {
                await agent.ReadCts.CancelAsync();
                await agent.Runtime.TerminateAsync(TimeSpan.FromSeconds(5));
            } catch {
                /* best-effort */
            }
        }
    }

    async Task PrepareForShutdownResumeAsync(AgentInstance agent) {
        try {
            await PrepareForResumeAsync(agent, CancellationToken.None);
        } catch (Exception ex) {
            LogCleanupStepFailed(ex, "stopping for resume", agent.Id);

            try { await agent.Runtime.TerminateAsync(TimeSpan.FromSeconds(5)); } catch { /* best-effort */ }
        }

        try { await agent.ReadCts.CancelAsync(); } catch { /* best-effort */ }
    }

    /// <see cref="_shutdownCts"/> is already cancelled here, so the parks run on their own budget.
    async Task ParkReviewersForShutdownAsync(IReadOnlyList<AgentInstance> reviewers) {
        if (reviewers.Count == 0) return;

        using var budget = new CancellationTokenSource(ShutdownReportBudget, _time);

        await Task.WhenAll(reviewers.Select(r => ParkReviewerAsync(
            new ReapCandidate(r, ReviewerParkedResumableReason, r.ActivityClock.ActivitySeq, FencedOnActivity: false) { Park = true },
            budget.Token)));
    }

    bool TryMarkResuming(AgentInstance agent) {
        if (ResumeLedger is not { } ledger || !IsInteractive(agent)) return false;

        lock (_ledgerLock) {
            if (agent.IsCleanupStarted || TryReadLedger(agent.Id) is not { } entry) return false;

            var now = _time.GetUtcNow();

            try {
                ledger.Write(entry with {
                    State                 = ResumeState.Resuming,
                    PredecessorInstanceId = _config.InstanceId,
                    ResumingAt            = now,
                    UpdatedAt             = now,
                    SessionId             = agent.SessionId ?? entry.SessionId,
                    VendorSession         = agent.VendorSession ?? entry.VendorSession
                });
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                LogResumeLedgerFailed(ex, "writing", agent.Id);
                return false;
            }

            agent.ResumePending = true;
            return true;
        }
    }

    /// Written through a temp file created owner-only, so the ring is never readable by anyone else.
    void TryWriteRing(string agentId, byte[] ring) {
        var path = ResumeLedger!.RingPath(agentId);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];

        try {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerOnlyFile;

            using (var stream = new FileStream(temp, options)) stream.Write(ring);

            File.Move(temp, path, overwrite: true);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            try { File.Delete(temp); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { /* best-effort */ }
            LogResumeRingFailed(ex, agentId);
        }
    }

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Resume ledger: failed writing the terminal ring for agent {AgentId}")]
    partial void LogResumeRingFailed(Exception ex, string agentId);
}
