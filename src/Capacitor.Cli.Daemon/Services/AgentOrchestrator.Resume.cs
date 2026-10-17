using System.Collections.Concurrent;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Continuation;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Resume;
using Capacitor.Cli.Core.WorkItems;
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

    int _launchesInAdmission;

    /// Launches the fence has admitted whose ledger entry is not yet written; a restart in that
    /// window would lose the agent.
    internal int LaunchesInAdmission => Volatile.Read(ref _launchesInAdmission);

    /// Non-interactive agents (flow participants) in the middle of a turn.
    internal int FlowParticipantsMidRound =>
        _agents.Values.Count(a => !IsInteractive(a) && a.Status is "Starting" or "Running" && a.ActivityClock.TurnInFlight);

    /// True when every live interactive agent is waiting on input, so a restart interrupts no turn.
    internal bool InteractiveAgentsAtTurnBoundary =>
        _agents.Values.Where(a => IsInteractive(a) && a.Status is "Starting" or "Running").All(a => a.ActivityClock.AwaitingInput);

    LaunchHold BeginLaunchHold() {
        Interlocked.Increment(ref _launchesInAdmission);

        return new LaunchHold(this);
    }

    /// Releases its admission count exactly once, however many times it is disposed.
    sealed class LaunchHold(AgentOrchestrator owner) : IDisposable {
        int _released;

        public void Dispose() {
            if (Interlocked.Exchange(ref _released, 1) == 0) Interlocked.Decrement(ref owner._launchesInAdmission);
        }
    }

    internal static readonly byte[] RestartMarker = "\r\n── kcap daemon restarted; session resumed ──\r\n"u8.ToArray();

    internal ShutdownIntent ShutdownIntent { get; set; } = ShutdownIntent.KeepAgents;

    /// Slices a replayed ring so the buffer evicts it gradually rather than whole on the next chunk.
    const int RingReplaySlice = 64 * 1024;

    const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// Grace for a shutdown stop, graceful request and exit together. Five seconds, then a one-second
    /// SIGTERM, keeps a keep-agents shutdown, whose stops and parks all run at once, well inside
    /// launchd's 20-second exit budget.
    internal TimeSpan ShutdownStopGrace { get; set; } = TimeSpan.FromSeconds(5);

    static readonly TimeSpan ShutdownTerminateGrace = TimeSpan.FromSeconds(1);

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

        // The vendor saves its session on a graceful exit; a resume reopens what it saved.
        try {
            await AwaitGracefulExitAsync(agent, ShutdownStopGrace, ct);
        } catch (Exception ex) {
            LogGracefulExitFailed(ex, agent.Id);
        }

        await agent.Runtime.TerminateAsync(ShutdownTerminateGrace);
    }

    /// Asks for a graceful exit and waits for it, request and exit together within
    /// <paramref name="grace"/>. A request that has not returned by then is abandoned, not awaited:
    /// a PTY write to a child that stopped reading never returns until the terminate after it.
    async Task AwaitGracefulExitAsync(AgentInstance agent, TimeSpan grace, CancellationToken ct) {
        var started  = _time.GetTimestamp();
        var graceful = Task.CompletedTask;

        try {
            graceful = agent.Runtime.RequestGracefulStopAsync();
            await graceful.WaitAsync(grace, _time, ct);
        } catch {
            if (!graceful.IsCompleted) ObserveAbandonedGracefulStop(graceful, agent.Id);
            throw;
        }

        var remaining = grace - _time.GetElapsedTime(started);
        if (remaining > TimeSpan.Zero) await agent.Runtime.WaitForExitAsync(remaining);
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
    /// stop for resume and resumable reviewers park; everything else is terminated. Every agent stops
    /// at once, so the total is one agent's stop however many run, and a park's durable ack goes out
    /// at the start of the exit budget.
    async Task StopAgentsForShutdownAsync(IReadOnlyList<AgentInstance> live) {
        // _shutdownCts is already cancelled here, so the parks run on their own budget.
        using var parkBudget = new CancellationTokenSource(ShutdownReportBudget, _time);
        var       keep       = ShutdownIntent == ShutdownIntent.KeepAgents;

        await Task.WhenAll(live.Select(agent =>
            !keep                      ? TerminateForShutdownAsync(agent)
          : IsInteractive(agent)       ? PrepareForShutdownResumeAsync(agent)
          : IsResumableReviewer(agent) ? ParkForShutdownAsync(agent, parkBudget.Token)
          :                              TerminateForShutdownAsync(agent)));
    }

    async Task PrepareForShutdownResumeAsync(AgentInstance agent) {
        try {
            await PrepareForResumeAsync(agent, CancellationToken.None);
        } catch (Exception ex) {
            LogCleanupStepFailed(ex, "stopping for resume", agent.Id);
        }

        await TerminateForShutdownAsync(agent);
    }

    async Task ParkForShutdownAsync(AgentInstance reviewer, CancellationToken ct) {
        await ParkReviewerAsync(
            new ReapCandidate(reviewer, ReviewerParkedResumableReason, reviewer.ActivityClock.ActivitySeq, FencedOnActivity: false) { Park = true },
            ct);

        await TerminateForShutdownAsync(reviewer);
    }

    /// Idempotent: an agent a stop above already ended has nothing left to terminate.
    static async Task TerminateForShutdownAsync(AgentInstance agent) {
        try {
            await agent.ReadCts.CancelAsync();
            if (!agent.Runtime.HasExited) await agent.Runtime.TerminateAsync(TimeSpan.FromSeconds(5));
        } catch {
            /* best-effort */
        }
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
            FlowRole:         agent.FlowRole,
            WorktreeBranch:       agent.Worktree.Branch,
            WorktreeIsStandalone: agent.Worktree.IsStandalone,
            WorktreeSourceRepo:   agent.Worktree.SourceRepo);

        return NewRunningEntry(agent.Id, agent.Vendor, agent.RuntimeTransport, agent.SessionId, agent.VendorSession, launch, prior);
    }

    /// The entry an admitted server launch records before its runtime starts, from the command and
    /// the resolved worktree; no <see cref="AgentInstance"/> exists yet.
    ResumeLedgerEntry BuildPreStartLedgerEntry(
            LaunchAgentCommand cmd, string? prompt, string? model, string? effort, WorktreeInfo worktree,
            WorkLocation work, LaunchConsentOutcome consent, string? pinnedSessionId) {
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
            FlowRole:         cmd.FlowRole,
            WorktreeBranch:       worktree.Branch,
            WorktreeIsStandalone: worktree.IsStandalone,
            WorktreeSourceRepo:   worktree.SourceRepo);

        return NewRunningEntry(cmd.AgentId, cmd.Vendor, PreStartTransport, pinnedSessionId, pinnedSessionId, launch, prior: null);
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
            ResumingAt:            null,
            DaemonInstanceId:      _config.InstanceId);
    }

    /// A Claude session is named by the daemon so the ledger holds it before the transcript exists;
    /// only a launch that gets a ledger entry is pinned.
    static string? MintPinnedSessionId(LaunchAgentCommand cmd) =>
        cmd.Kind == LaunchKind.Default && string.Equals(cmd.Vendor, "claude", StringComparison.OrdinalIgnoreCase)
            ? Guid.NewGuid().ToString()
            : null;

    internal void WriteLedger(AgentInstance agent, LaunchAgentCommand? cmd, string origin, LaunchConsentOutcome? consent) {
        if (ResumeLedger is null || !IsInteractive(agent)) return;

        lock (_ledgerLock) {
            if (agent.IsCleanupStarted) return;
            TryWriteLedger(agent.Id, () => BuildLedgerEntry(agent, cmd, origin, consent));
        }
    }

    void WritePreStartLedger(
            LaunchAgentCommand cmd, string? prompt, string? model, string? effort, WorktreeInfo worktree,
            WorkLocation work, LaunchConsentOutcome consent, string? pinnedSessionId) {
        if (ResumeLedger is null || cmd.Kind != LaunchKind.Default) return;

        lock (_ledgerLock) {
            // A live agent under this id owns the entry; a colliding launch must not overwrite it.
            if (_agents.ContainsKey(cmd.AgentId)) return;
            TryWriteLedger(cmd.AgentId, () => BuildPreStartLedgerEntry(cmd, prompt, model, effort, worktree, work, consent, pinnedSessionId));
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

    /// <summary>What a resume launch carries past an ordinary admitted launch.</summary>
    internal sealed record ResumeLaunch(ResumeLedgerEntry Entry, WorktreeInfo Worktree) {
        /// <summary>The instance the launch built, once it exists.</summary>
        public AgentInstance? Agent { get; set; }

        public WorkLocation Work      => Entry.Launch.Work;
        public bool         IsPrivate => Entry.Launch.IsPrivate;
        public string       Origin    => Entry.Launch.Origin;
    }

    const string NativeResumeUnsupported = "native_resume_unsupported";
    const string StoppedDuringResume     = "stopped";

    static readonly TimeSpan ReadinessPoll = TimeSpan.FromMilliseconds(50);

    readonly ConcurrentDictionary<string, ResumeLedgerEntry> _resumeCandidates = new(StringComparer.Ordinal);
    readonly List<ResumeLedgerEntry> _resumeOrder = [];
    readonly List<(ResumeLedgerEntry Entry, string Reason)> _interruptedAtBoot = [];
    string[] _ledgerWorktrees = [];

    SemaphoreSlim ResumeLane => field ??= new SemaphoreSlim(Math.Max(1, _config.ResumeConcurrency));

    /// <summary>The kcap continue fallback, tried when a native resume is unavailable or fails.</summary>
    internal Func<ResumeLedgerEntry, string, CancellationToken, Task<ResumeOutcome>> ContinueEntry {
        get => field ??= ContinueEntryAsync;
        set;
    }

    /// <summary>Sorts the predecessor's ledger into candidates and entries interrupted at boot. Runs
    /// after the orphan reaper, whose unresolved records it reads, and before the server connect that
    /// claims the candidates.</summary>
    internal void LoadResumeLedger() {
        if (ResumeLedger is not { } ledger) return;

        IReadOnlyList<ResumeLedgerEntry> entries;

        try {
            entries = ledger.ReadAll();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            LogResumeLedgerUnreadable(ex);
            return;
        }

        var blocked = (_orphanReaper?.BlockedCandidates() ?? []).Select(c => c.AgentId).ToHashSet(StringComparer.Ordinal);
        var beat    = DaemonHeartbeat.LastBeat(_config.Store, _config.Name);
        var now     = _time.GetUtcNow();

        _ledgerWorktrees = [.. entries.Where(e => e.Launch.Work == WorkLocation.OwnedWorktree).Select(e => e.Launch.WorktreePath)];

        foreach (var entry in entries) {
            if (entry.State == ResumeState.Interrupted) continue;

            var reason = !_config.AutoResume                                 ? "auto_resume_off"
                       : now - LastAlive(entry, beat) > _config.ResumeMaxAge ? "too_old"
                       : blocked.Contains(entry.AgentId)                     ? "predecessor_process_unresolved"
                       : !Directory.Exists(entry.Launch.WorktreePath)        ? "worktree_missing"
                       :                                                       null;

            if (reason is null) {
                _resumeCandidates[entry.AgentId] = entry;
                _resumeOrder.Add(entry);
            } else {
                InterruptEntry(entry.AgentId, reason);
                _interruptedAtBoot.Add((entry, reason));
            }
        }
    }

    /// <summary>The later of the entry's resuming stamp and the predecessor's last heartbeat; an entry
    /// with neither was last known alive when it was written.</summary>
    static DateTimeOffset LastAlive(ResumeLedgerEntry entry, DateTimeOffset? beat) =>
        (entry.ResumingAt, beat) switch {
            ({ } resuming, { } b) => resuming > b ? resuming : b,
            ({ } resuming, null)  => resuming,
            (null, { } b)         => b,
            _                     => entry.UpdatedAt
        };

    /// <summary>The candidates still waiting on their resume, private agents excluded.</summary>
    internal ResumingAgentClaim[] ResumingClaims() => [
        .. _resumeCandidates.Values
            .Where(e => !e.Launch.IsPrivate)
            .Select(e => new ResumingAgentClaim(e.AgentId, e.PredecessorInstanceId ?? e.DaemonInstanceId ?? "", e.SessionId))
    ];

    /// <summary>Every owned worktree the ledger names, interrupted entries included: a later manual
    /// resume needs its worktree as much as a boot candidate does.</summary>
    internal IReadOnlyList<string> LedgerWorktreePaths() => _ledgerWorktrees;

    /// <summary>The boot resume lane: reports the entries interrupted at boot, then resumes the
    /// candidates at most <see cref="DaemonConfig.ResumeConcurrency"/> at a time. Never throws.</summary>
    internal async Task ResumeOnBootAsync(CancellationToken ct) {
        try {
            foreach (var (entry, reason) in _interruptedAtBoot.Where(i => !i.Entry.Launch.IsPrivate))
                await ReportResumeFailedAsync(entry, reason, null, ct);

            await Task.WhenAll(_resumeOrder.Select(entry => RunResumeThroughLaneAsync(entry, ct)));
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
        } catch (Exception ex) {
            LogResumeLaneFailed(ex);
        }
    }

    async Task RunResumeThroughLaneAsync(ResumeLedgerEntry entry, CancellationToken ct) {
        await ResumeLane.WaitAsync(ct);

        try {
            await ResumeEntryAsync(entry, ct);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            LogResumeFailed(ex, entry.AgentId);
        } finally {
            _resumeCandidates.TryRemove(entry.AgentId, out _);
            ResumeLane.Release();
        }
    }

    /// <summary>An operator's retry of an interrupted entry: <c>resuming</c> once the entry is marked
    /// resuming and queued on the boot lane's gate, <c>not_interrupted</c> for any other state,
    /// <c>unknown</c> without an entry. The age cap and <see cref="DaemonConfig.AutoResume"/> do not
    /// apply to an explicit retry.</summary>
    internal Task<string> ResumeInterruptedAsync(string agentId, CancellationToken ct) {
        ResumeLedgerEntry resuming;

        lock (_ledgerLock) {
            if (TryReadLedger(agentId) is not { } entry) return Task.FromResult("unknown");
            if (entry.State != ResumeState.Interrupted) return Task.FromResult("not_interrupted");

            var now = _time.GetUtcNow();
            resuming = entry with { State = ResumeState.Resuming, ResumingAt = now, LastError = null, UpdatedAt = now };

            try {
                ResumeLedger!.Write(resuming);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                LogResumeLedgerFailed(ex, "writing", agentId);
                throw;
            }

            _resumeCandidates[agentId] = resuming;
        }

        _ = RunResumeThroughLaneAsync(resuming, _shutdownCts.Token);

        return Task.FromResult("resuming");
    }

    /// <summary>Native resume, then kcap continue, then interrupted.</summary>
    internal async Task<ResumeOutcome> ResumeEntryAsync(ResumeLedgerEntry entry, CancellationToken ct) {
        var native = entry.VendorSession is not null
                  && _runtimeFactories.TryGetValue(entry.Vendor, out var factory)
                  && factory.SupportsRestartResume(entry.Transport);

        string reason;
        int?   exitCode = null;

        if (native) {
            if (await NativeResumeAsync(entry, ct) is not { } failure) return ResumeOutcome.Resumed;

            (reason, exitCode) = failure;

            // The stop's own teardown ends the agent and removes what it kept.
            if (reason == StoppedDuringResume) return ResumeOutcome.Stopped;

            entry = RecordFailedAttempt(entry, LastErrorOf(reason, exitCode));
        } else {
            reason = NativeResumeUnsupported;
        }

        var outcome = await ContinueEntry(entry, reason, ct);
        if (outcome != ResumeOutcome.Interrupted) return outcome;

        InterruptEntry(entry.AgentId, LastErrorOf(reason, exitCode));
        if (!entry.Launch.IsPrivate) await ReportResumeFailedAsync(entry, reason, exitCode, ct);

        return ResumeOutcome.Interrupted;
    }

    static string LastErrorOf(string reason, int? exitCode) =>
        exitCode is { } code ? $"{reason} (exit code {code})" : reason;

    /// <summary>Relaunches the entry under its own agent id. Null when it registered and its runtime
    /// stayed alive through <see cref="DaemonConfig.ResumeReadinessWindow"/>, else why not.</summary>
    async Task<(string Reason, int? ExitCode)?> NativeResumeAsync(ResumeLedgerEntry entry, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();

        using var admission = _admission.TryAdmit();
        if (admission is null) return ("daemon_renaming", null);

        using var hold = BeginLaunchHold();

        var input = new LaunchConsentInput(
            entry.Launch.RequesterUserId, RequesterIsOwner: false, LaunchConsentEngine.KindToken(LaunchKind.Default),
            entry.Launch.RepoPath, entry.Vendor, entry.Launch.RequesterDisplay);
        var consent = _consentGate.GrantResume(entry.AgentId, input, entry.Launch.ConsentSource ?? "");

        var resume  = new ResumeLaunch(entry, ResumedWorktree(entry));
        var outcome = await LaunchAdmittedAsync(BuildResumeCommand(entry, LastAlive(entry, null)), consent, hold, resume);

        if (outcome.Kind == CommandOutcomeKind.LaunchRejected)
            return (outcome.RejectReason == CommandRejectedReason.DaemonCapacity ? "daemon_capacity" : "launch_rejected", null);

        if (resume.Agent is not { } agent) return ("runtime_exited", null);

        var survived = outcome.Kind == CommandOutcomeKind.LaunchExecuted && await SurvivesReadinessAsync(agent, ct);

        if (agent.StopRequested) return (StoppedDuringResume, null);
        if (!survived) return ("runtime_exited", agent.Runtime.ExitCode);

        lock (_ledgerLock) {
            // A shutdown that began meanwhile has marked the agent for its own resume.
            if (!agent.IsCleanupStarted && !_shutdownCts.IsCancellationRequested) agent.ResumePending = false;
        }

        return null;
    }

    /// <summary>Whether the resumed runtime is still running, and still this agent, once the readiness
    /// window has passed since it published.</summary>
    async Task<bool> SurvivesReadinessAsync(AgentInstance agent, CancellationToken ct) {
        var started = _time.GetTimestamp();

        while (!agent.Runtime.HasExited && _agents.TryGetValue(agent.Id, out var live) && ReferenceEquals(live, agent)) {
            var left = _config.ResumeReadinessWindow - _time.GetElapsedTime(started);
            if (left <= TimeSpan.Zero) return true;

            await Task.Delay(left < ReadinessPoll ? left : ReadinessPoll, _time, ct);
        }

        return false;
    }

    const string SupersededReason = "superseded";

    internal TimeSpan ContinueSummaryBudget { get; set; } = TimeSpan.FromSeconds(10);

    string ServerBase => _config.ServerUrl.TrimEnd('/');

    /// <summary>Starts a fresh agent under a new id in the entry's worktree, then ends the entry's agent
    /// and session as superseded; the successor's session takes over the old one's work items and plans
    /// once its id is known. <see cref="ResumeOutcome.Interrupted"/>, with nothing reported, when the
    /// fresh agent does not launch: the lane's failed-resume report is then the old agent's only end.</summary>
    internal async Task<ResumeOutcome> ContinueEntryAsync(ResumeLedgerEntry entry, string reason, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();

        var summary = await ReadPreviousSummaryAsync(entry.SessionId, ct);
        var prompt  = ContinuePrompt.Build(entry.Launch.Prompt, entry.SessionId, reason, summary, LastAlive(entry, null));

        if (await LaunchContinuationAsync(entry, Guid.NewGuid().ToString("N"), prompt) is not { } agent)
            return ResumeOutcome.Interrupted;

        // The successor's own entry now holds the worktree.
        DeleteLedger(entry.AgentId);

        // A private agent was never registered, so the server holds no agent to end.
        if (!entry.Launch.IsPrivate)
            await _server.ReportAgentEndedForShutdownAsync(entry.AgentId, entry.SessionId, "Completed", SupersededReason, null, ct);

        if (entry.SessionId is { } previous) StartContinuationTakeover(agent, previous);

        return ResumeOutcome.Continued;
    }

    /// <summary>Runs the takeover off the lane, once, as soon as the successor has both its own session
    /// id and the session it continues: either may arrive first, the id at publish or by discovery.</summary>
    void StartContinuationTakeover(AgentInstance agent, string? previousSession = null) {
        string previous, current;

        lock (_ledgerLock) {
            if (previousSession is not null) agent.ContinuedFromSessionId = previousSession;
            if (agent.ContinuedFromSessionId is not { } from || agent.SessionId is not { } own) return;

            agent.ContinuedFromSessionId = null;
            (previous, current) = (from, own);
        }

        _ = TakeOverAsync(agent.Id, previous, current, _shutdownCts.Token);
    }

    /// <summary>The launched successor, or null when it did not launch; a successor that did not
    /// launch leaves no ledger entry behind.</summary>
    async Task<AgentInstance?> LaunchContinuationAsync(ResumeLedgerEntry entry, string agentId, string prompt) {
        using var admission = _admission.TryAdmit();
        if (admission is null) return null;

        using var hold = BeginLaunchHold();

        var input = new LaunchConsentInput(
            entry.Launch.RequesterUserId, RequesterIsOwner: false, LaunchConsentEngine.KindToken(LaunchKind.Default),
            entry.Launch.RepoPath, entry.Vendor, entry.Launch.RequesterDisplay);
        var consent = _consentGate.GrantContinue(agentId, input, entry.Launch.ConsentSource ?? "");

        var successor = entry with {
            AgentId = agentId, SessionId = null, VendorSession = null, Attempts = 0, LastError = null, CreatedAt = _time.GetUtcNow()
        };
        var launch  = new ResumeLaunch(successor, ResumedWorktree(entry));
        var cmd     = BuildResumeCommand(entry, LastAlive(entry, null)) with { AgentId = agentId, Prompt = prompt, ResumeSessionId = null };
        var outcome = await LaunchAdmittedAsync(cmd, consent, hold, launch);

        if (launch.Agent is { } agent && outcome.Kind == CommandOutcomeKind.LaunchExecuted) {
            lock (_ledgerLock) {
                if (!agent.IsCleanupStarted) {
                    if (!_shutdownCts.IsCancellationRequested) agent.ResumePending = false;
                    return agent;
                }
            }
        }

        DiscardFailedLaunchLedger(agentId);
        return null;
    }

    /// <summary>The previous session as the server summarises it, or null on any failure or after
    /// <see cref="ContinueSummaryBudget"/>: the summary only enriches the prompt.</summary>
    async Task<string?> ReadPreviousSummaryAsync(string? sessionId, CancellationToken ct) {
        if (WorkContextIds.CanonicalSessionId(sessionId) is not { } id) return null;

        using var budget = new CancellationTokenSource(ContinueSummaryBudget, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);

        try {
            using var client   = await _http.ForBackgroundAsync(linked.Token);
            using var response = await client.GetAsync($"{ServerBase}/api/sessions/{Uri.EscapeDataString(id)}/summary", linked.Token);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync(linked.Token);

            return JsonSerializer.Deserialize(body, CapacitorJsonContext.Default.SessionSummaryDto) is { } dto
                ? ContinuePrompt.Summarize(dto)
                : null;
        } catch (Exception ex) when (!ct.IsCancellationRequested) {
            LogContinueSummaryUnavailable(ex, id);
            return null;
        }
    }

    /// <summary>Never throws: it runs unobserved.</summary>
    async Task TakeOverAsync(string agentId, string previous, string current, CancellationToken ct) {
        try {
            using var client = await _http.ForBackgroundAsync(ct);
            var result = await new SessionTakeover(new ProvenExitedLiveness(), _time)
                .RunAsync(client, ServerBase, previous, current, force: false, ct);

            LogContinueTakeover(agentId, previous, current, result switch {
                TakeoverResult.Completed { Unsuccessful: false } => "completed",
                TakeoverResult.Completed                         => "partial",
                TakeoverResult.Refused r                         => $"refused: {r.Reason}",
                TakeoverResult.Failed f                          => $"failed: {f.Reason}",
                _                                                => "unauthorized"
            });
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
        } catch (Exception ex) {
            LogContinueTakeoverFailed(ex, agentId, previous);
        }
    }

    static LaunchAgentCommand BuildResumeCommand(ResumeLedgerEntry entry, DateTimeOffset interruptedAt) {
        var launch = entry.Launch;

        return new LaunchAgentCommand(
            AgentId:             entry.AgentId,
            Prompt:              ResumeNudge.Build(interruptedAt, launch.Prompt),
            Model:               launch.Model ?? "",
            Effort:              launch.Effort,
            RepoPath:            launch.RepoPath,
            Tools:               null,
            AttachmentIds:       null,
            Vendor:              entry.Vendor,
            FlowRunId:           launch.FlowRunId,
            FlowRole:            launch.FlowRole,
            CodexPosture:        launch is { SandboxPolicy: { } sandbox, ApprovalPolicy: { } approval } ? new CodexLaunchPosture(sandbox, approval) : null,
            RequesterUserId:     launch.RequesterUserId,
            RequesterDisplay:    launch.RequesterDisplay,
            AcpPermissionPreset: launch.PermissionPreset,
            ResumeSessionId:     entry.VendorSession,
            Title:               launch.Title);
    }

    /// <summary>The ledger's worktree as the launch path names it.</summary>
    static WorktreeInfo ResumedWorktree(ResumeLedgerEntry entry) {
        var launch = entry.Launch;
        var path   = launch.WorktreePath;

        if (launch.Work == WorkLocation.BorrowedCwd) return WorktreeInfo.Borrowed(path);

        // An entry written before the worktree fields existed carries none of them.
        return new WorktreeInfo(
            path,
            launch.WorktreeBranch ?? $"capacitor/{Path.GetFileName(path)}",
            launch.WorktreeSourceRepo ?? launch.RepoPath,
            IsStandalone: launch.WorktreeIsStandalone ?? Directory.Exists(Path.Combine(path, ".git"))) {
            GitRelativeCwd = launch.GitRelativeCwd
        };
    }

    /// <summary>Replays the predecessor's persisted ring into a resumed agent, then removes it.</summary>
    void ReplayPersistedRing(AgentInstance agent) {
        var path = ResumeLedger!.RingPath(agent.Id);
        if (!File.Exists(path)) return;

        try {
            ReplayRing(agent, File.ReadAllBytes(path));
            File.Delete(path);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            LogResumeRingFailed(ex, agent.Id);
        }
    }

    /// <summary>Rewrites a resumed agent's entry as running under this daemon, keeping the original
    /// launch descriptor: the agent's own prompt is the nudge.</summary>
    void WriteResumedLedger(AgentInstance agent, ResumeLedgerEntry entry) {
        if (ResumeLedger is null) return;

        lock (_ledgerLock) {
            if (agent.IsCleanupStarted) return;

            TryWriteLedger(agent.Id, () => entry with {
                DaemonId              = _daemonId,
                DaemonInstanceId      = _config.InstanceId,
                UpdatedAt             = _time.GetUtcNow(),
                Transport             = agent.RuntimeTransport,
                SessionId             = agent.SessionId ?? entry.SessionId,
                VendorSession         = agent.VendorSession ?? entry.VendorSession,
                State                 = ResumeState.Running,
                LastError             = null,
                PredecessorInstanceId = null,
                ResumingAt            = null
            });
        }
    }

    ResumeLedgerEntry RecordFailedAttempt(ResumeLedgerEntry entry, string reason) {
        var failed = entry with { Attempts = entry.Attempts + 1, LastError = reason, UpdatedAt = _time.GetUtcNow() };

        lock (_ledgerLock) TryWriteLedger(entry.AgentId, () => failed);

        return failed;
    }

    void InterruptEntry(string agentId, string reason) {
        lock (_ledgerLock) {
            try {
                ResumeLedger?.MarkInterrupted(reason, e => e.AgentId == agentId);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                LogResumeLedgerFailed(ex, "interrupting", agentId);
            }
        }
    }

    Task ReportResumeFailedAsync(ResumeLedgerEntry entry, string reason, int? exitCode, CancellationToken ct) =>
        _server.ReportAgentEndedForShutdownAsync(entry.AgentId, entry.SessionId, "Failed", $"resume_failed:{reason}", exitCode, ct);

    /// <summary>A private agent's launch reports nothing to the server, failures included.</summary>
    Task LaunchFailedUnlessPrivateAsync(bool isPrivate, string agentId, string reason) =>
        isPrivate ? Task.CompletedTask : _server.LaunchFailedAsync(agentId, reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Continue: the summary of previous session {SessionId} is unavailable")]
    partial void LogContinueSummaryUnavailable(Exception ex, string sessionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Continue: agent {AgentId} session {Current} took over {Previous}: {Outcome}")]
    partial void LogContinueTakeover(string agentId, string previous, string current, string outcome);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Continue: agent {AgentId} could not take over session {Previous}")]
    partial void LogContinueTakeoverFailed(Exception ex, string agentId, string previous);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Resume ledger: the ledger could not be read; nothing resumes")]
    partial void LogResumeLedgerUnreadable(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Resume: resuming agent {AgentId} failed")]
    partial void LogResumeFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Error, Message = "Resume: the boot resume lane failed")]
    partial void LogResumeLaneFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Resume ledger: failed {Step} the entry for agent {AgentId}")]
    partial void LogResumeLedgerFailed(Exception ex, string step, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Resume ledger: failed writing the terminal ring for agent {AgentId}")]
    partial void LogResumeRingFailed(Exception ex, string agentId);
}
