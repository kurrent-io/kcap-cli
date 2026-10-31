using System.Collections.Concurrent;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Continuation;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Resume;
using Capacitor.Cli.Core.WorkItems;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// The resume ledger: one durable entry per interactive agent, and per flow participant whose vendor can
/// reopen its reviewer session, written at launch, rewritten as its session ids are learned, deleted at a
/// normal end. Participants' entries live in their own store, which a daemon that cannot resume them
/// never reads.
internal partial class AgentOrchestrator {
    /// The transport an entry records before its runtime exists: a value no factory maps, so a resume
    /// of such an entry takes the daemon's own route. The rewrite from the live instance replaces it.
    const string PreStartTransport = "pending";

    readonly Lock _ledgerLock = new();

    internal ResumeLedgerStore? ResumeLedger { get; }

    internal ResumeLedgerStore? ParticipantLedger { get; }

    /// <summary>The ids whose entries are in <see cref="ParticipantLedger"/>.</summary>
    readonly ConcurrentDictionary<string, byte> _participantEntries = new(StringComparer.Ordinal);

    ResumeLedgerStore? StoreFor(string agentId) =>
        _participantEntries.ContainsKey(agentId) ? ParticipantLedger : ResumeLedger;

    static bool IsInteractive(AgentInstance a) => a.Kind == LaunchKind.Default;

    /// <summary>A flow participant a successor can resume in place. A Codex app-server reviewer parks
    /// instead, and a private one has no flow to continue.</summary>
    bool IsResumableParticipant(AgentInstance a) =>
        a.Kind == LaunchKind.ReviewFlow && !a.IsPrivate && !IsResumableReviewer(a)
     && _runtimeFactories.TryGetValue(a.Vendor, out var factory)
     && factory.SupportsReviewerRestartResume(a.RuntimeTransport, a.Work);

    bool IsLedgered(AgentInstance a) => IsInteractive(a) || IsResumableParticipant(a);

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

    /// <summary>One resume in flight, keyed by its entry's agent id. A stop of that id or an end-agents
    /// shutdown withdraws it: it then publishes no agent and writes no entry.</summary>
    sealed class ResumeAttempt(CancellationToken outer) : IDisposable {
        readonly CancellationTokenSource _cts = CancellationTokenSource.CreateLinkedTokenSource(outer);

        /// <summary>Read and set under the ledger lock.</summary>
        public bool Withdrawn { get; set; }

        /// <summary>Withdrawn by a stop, which leaves the entry's worktree and attachments to the attempt's
        /// end. Read and set under the ledger lock.</summary>
        public bool Discarded { get; set; }

        public CancellationToken Token => _cts.Token;

        /// <summary>Called outside the ledger lock: cancellation runs its callbacks inline.</summary>
        public void Cancel() {
            try {
                _cts.Cancel();
            } catch (ObjectDisposedException) {
                // The attempt finished first.
            }
        }

        public void Dispose() => _cts.Dispose();
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

        if (!keep) WithdrawRecoveryForEndAgents();

        await Task.WhenAll(live.Select(agent =>
            !keep                      ? TerminateForShutdownAsync(agent)
          : IsResumableReviewer(agent) ? ParkForShutdownAsync(agent, parkBudget.Token)
          : IsLedgered(agent)          ? PrepareForShutdownResumeAsync(agent)
          :                              TerminateForShutdownAsync(agent)));
    }

    /// <summary>An end-agents shutdown ends what recovery holds too: every live agent loses its pending
    /// resume, so its teardown removes its entry and worktree, and every queued or in-flight resume is
    /// withdrawn with its entry and ring.</summary>
    void WithdrawRecoveryForEndAgents() {
        List<ResumeAttempt> withdrawn = [];

        lock (_ledgerLock) {
            foreach (var agent in _agents.Values) agent.ResumePending = false;

            foreach (var agentId in _resumeCandidates.Keys.Concat(_resumeAttempts.Keys).Distinct(StringComparer.Ordinal).ToList()) {
                _resumeCandidates.TryRemove(agentId, out _);

                if (_resumeAttempts.TryGetValue(agentId, out var attempt)) {
                    attempt.Withdrawn = true;
                    withdrawn.Add(attempt);
                }

                // A resumed agent already live under this id deletes its own entry in its teardown.
                if (!_agents.ContainsKey(agentId)) DeleteLedger(agentId);
            }

            // A resume the host stop already cancelled left neither candidate nor attempt behind, only its
            // entry, which nothing live would delete and the next start would resume.
            foreach (var entry in LedgerEntriesForEndAgents())
                if (entry.State != ResumeState.Interrupted && !_agents.ContainsKey(entry.AgentId)) DeleteLedger(entry.AgentId);

            // A tombstone stays: it holds a worktree until its process is proven gone.
            foreach (var entry in ParticipantEntriesForEndAgents()) {
                if (IsTombstone(entry) || _agents.ContainsKey(entry.AgentId)) continue;

                _participantEntries[entry.AgentId] = 0;
                DeleteLedger(entry.AgentId);
            }
        }

        foreach (var attempt in withdrawn) attempt.Cancel();
    }

    IReadOnlyList<ResumeLedgerEntry> ParticipantEntriesForEndAgents() {
        try {
            return ParticipantLedger?.ReadAll() ?? [];
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            LogResumeLedgerFailed(ex, "reading", "*");
            return [];
        }
    }

    IReadOnlyList<ResumeLedgerEntry> LedgerEntriesForEndAgents() {
        try {
            return ResumeLedger?.ReadAll() ?? [];
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            LogResumeLedgerFailed(ex, "reading", "*");
            return [];
        }
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

    /// How long a daemon write waits for a session end holding the entry's lock.
    static readonly TimeSpan EntryLockWait = TimeSpan.FromSeconds(1);

    bool TryMarkResuming(AgentInstance agent) {
        if (StoreFor(agent.Id) is not { } ledger || !IsLedgered(agent)) return false;

        lock (_ledgerLock) {
            if (agent.IsCleanupStarted) return false;

            var now   = _time.GetUtcNow();
            var clock = agent.ActivityClock.Snapshot();
            LockedUpdate result;

            try {
                result = ledger.TryUpdateUnderLock(agent.Id, entry => {
                    var vendorSession = agent.VendorSession ?? entry.VendorSession;

                    // A participant has no kcap continue to fall back on, so one with no session to reopen ends here.
                    if (EndCommittedForCurrentSession(entry, agent) || (entry.Participant is not null && vendorSession is null)) return null;

                    return entry with {
                        State                 = ResumeState.Resuming,
                        PredecessorInstanceId = _config.InstanceId,
                        ResumingAt            = now,
                        UpdatedAt             = now,
                        AwaitingInput         = agent.ActivityClock.AwaitingInput,
                        SessionId             = agent.SessionId ?? entry.SessionId,
                        VendorSession         = vendorSession,
                        Participant           = entry.Participant is { } p ? p with { ActivitySeq = clock.ActivitySeq, AgeMs = clock.AgeMs } : null
                    };
                }, EntryLockWait);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                LogResumeLedgerFailed(ex, "writing", agent.Id);
                return false;
            }

            if (result != LockedUpdate.Written) return false;

            agent.ResumePending = true;
            return true;
        }
    }

    /// <summary>An end committed for the session the agent now runs. One committed for an earlier session
    /// of the same process — a Claude <c>/clear</c> ends one session and starts the next — does not count;
    /// an unknown session on either side does.</summary>
    static bool EndCommittedForCurrentSession(ResumeLedgerEntry entry, AgentInstance agent) {
        if (entry.EndCommittedAt is null) return false;

        var ended   = WorkContextIds.CanonicalSessionId(entry.EndCommittedSession);
        var current = WorkContextIds.CanonicalSessionId(agent.SessionId ?? entry.SessionId);

        return ended is null || current is null || string.Equals(ended, current, StringComparison.OrdinalIgnoreCase);
    }

    /// Written through a temp file created owner-only, so the ring is never readable by anyone else.
    void TryWriteRing(string agentId, byte[] ring) {
        var path = StoreFor(agentId)!.RingPath(agentId);
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

    /// <param name="permissionMode">A local launch's mode, which has no <paramref name="cmd"/> to carry it.</param>
    internal ResumeLedgerEntry BuildLedgerEntry(
            AgentInstance agent, LaunchAgentCommand? cmd, string origin, LaunchConsentOutcome? consent, string? permissionMode = null) {
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
            TitleDerived:     agent.StartTitle?.Derived ?? true,
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
            WorktreeSourceRepo:   agent.Worktree.SourceRepo,
            PermissionMode:         cmd?.PermissionMode ?? permissionMode ?? prior?.Launch.PermissionMode,
            InactivityBoundSeconds: agent.InactivityBoundSeconds,
            AccountDirectory:       agent.Account?.Directory,
            AccountId:              agent.Account?.AccountId,
            ScratchWorkspace:       cmd is { ScratchWorkspace: true } ? true : prior?.Launch.ScratchWorkspace,
            WorktreeOwnsBranch:     agent.Worktree.OwnsBranch,
            AccountLabel:           agent.Account?.Label,
            HandoffSource:          agent.HandoffSource);

        return NewRunningEntry(agent.Id, agent.Vendor, agent.RuntimeTransport, agent.SessionId, agent.VendorSession, launch, prior) with {
            HasTerminal = agent.Runtime.EmitsTerminalOutput, TranscriptFormat = TranscriptFormatOf(agent),
            StartedAt = StartedAtOf(agent),
            Participant = IsInteractive(agent) ? null : ParticipantOf(agent, cmd, prior?.Participant),
        };
    }

    /// <summary>The launch command is only at hand on the first write; a rewrite keeps what that one recorded.</summary>
    static ParticipantResume ParticipantOf(AgentInstance agent, LaunchAgentCommand? cmd, ParticipantResume? prior) =>
        new(agent.Kind,
            cmd?.McpAllowlist ?? prior?.McpAllowlist,
            cmd?.Tools ?? prior?.Tools,
            cmd?.ReviewerCertification is { } certification
                ? certification with { ExpectedDaemonConnectionId = "", ExpectedCliVersion = "" }
                : prior?.Certification,
            prior?.ActivitySeq ?? 0,
            prior?.AgeMs ?? 0,
            prior?.LastRoundInput,
            prior?.LastRoundAt);

    /// <summary>The round a participant was last sent, which only it carries the token of: a resume
    /// repeats it.</summary>
    void RecordRoundInput(AgentInstance agent, string input) {
        if (!_participantEntries.ContainsKey(agent.Id)) return;

        lock (_ledgerLock) {
            if (agent.IsCleanupStarted || TryReadLedger(agent.Id) is not { Participant: { } participant } entry) return;

            TryWriteLedger(agent.Id, () => entry with {
                Participant = participant with { LastRoundInput = input, LastRoundAt = _time.GetUtcNow() }, UpdatedAt = _time.GetUtcNow()
            });
        }
    }

    /// The entry an admitted server launch records before its runtime starts, from the command and
    /// the resolved worktree; no <see cref="AgentInstance"/> exists yet.
    ResumeLedgerEntry BuildPreStartLedgerEntry(
            LaunchAgentCommand cmd, string? prompt, string? model, string? effort, WorktreeInfo worktree,
            WorkLocation work, LaunchConsentOutcome consent, string? pinnedSessionId, ResolvedAccount? account) {
        var launch = new ResumeLaunchDescriptor(
            Prompt:           prompt,
            Model:            model,
            Effort:           effort,
            RepoPath:         cmd.RepoPath,
            WorktreePath:     worktree.Path,
            Work:             work,
            GitRelativeCwd:   worktree.GitRelativeCwd,
            SandboxPolicy:    cmd.CodexPosture?.Sandbox,
            ApprovalPolicy:   cmd.CodexPosture?.Approval,
            PermissionPreset: cmd.AcpPermissionPreset,
            McpConfigPath:    null,
            Title:            StartTitleOf(cmd)?.Text ?? AgentStartTitle.Shorten(prompt),
            TitleDerived:     StartTitleOf(cmd)?.Derived ?? true,
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
            WorktreeSourceRepo:   worktree.SourceRepo,
            PermissionMode:         cmd.PermissionMode,
            InactivityBoundSeconds: cmd.InactivityBoundSeconds,
            AccountDirectory:       account?.Directory,
            AccountId:              account?.AccountId,
            ScratchWorkspace:       cmd.ScratchWorkspace ? true : null,
            WorktreeOwnsBranch:     worktree.OwnsBranch,
            AccountLabel:           account?.Label,
            HandoffSource:          cmd.Handoff?.Source);

        return NewRunningEntry(cmd.AgentId, cmd.Vendor, PreStartTransport, null, pinnedSessionId ?? cmd.ResumeSessionId, launch, prior: null);
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
            DaemonInstanceId:      _config.InstanceId,
            RunEpoch:              _daemonEpoch,
            LaunchEpoch:           prior?.LaunchEpoch ?? _daemonEpoch);
    }

    /// A Claude session is named by the daemon so the ledger holds it before the transcript exists;
    /// only a launch that gets a ledger entry is pinned.
    static string? MintPinnedSessionId(LaunchAgentCommand cmd) =>
        cmd.Kind == LaunchKind.Default && string.Equals(cmd.Vendor, "claude", StringComparison.OrdinalIgnoreCase)
            ? Guid.NewGuid().ToString()
            : null;

    internal void WriteLedger(
            AgentInstance agent, LaunchAgentCommand? cmd, string origin, LaunchConsentOutcome? consent, string? permissionMode = null) {
        if (ResumeLedger is null || !IsLedgered(agent)) return;

        lock (_ledgerLock) {
            if (agent.IsCleanupStarted) return;
            if (!IsInteractive(agent)) _participantEntries[agent.Id] = 0;
            TryWriteLedger(agent.Id, () => BuildLedgerEntry(agent, cmd, origin, consent, permissionMode));
        }
    }

    void WritePreStartLedger(
            LaunchAgentCommand cmd, string? prompt, string? model, string? effort, WorktreeInfo worktree,
            WorkLocation work, LaunchConsentOutcome consent, string? pinnedSessionId, ResolvedAccount? account) {
        if (ResumeLedger is null || cmd.Kind != LaunchKind.Default) return;

        lock (_ledgerLock) {
            // A live agent under this id owns the entry; a colliding launch must not overwrite it.
            if (_agents.ContainsKey(cmd.AgentId)) return;
            TryWriteLedger(cmd.AgentId, () => BuildPreStartLedgerEntry(cmd, prompt, model, effort, worktree, work, consent, pinnedSessionId, account));
        }
    }

    internal void UpdateLedgerSession(AgentInstance agent) {
        if (ResumeLedger is null || !IsLedgered(agent)) return;

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
        if (StoreFor(agentId) is not { } ledger) return;

        lock (_ledgerLock) {
            try {
                ledger.Delete(agentId);
                _participantEntries.TryRemove(agentId, out _);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                LogResumeLedgerFailed(ex, "deleting", agentId);
            }
        }
    }

    /// <summary>Under the entry lock, carrying over an end a session committed meanwhile, unless
    /// <paramref name="newRun"/>: a resumed run starts with none.</summary>
    void TryWriteLedger(string agentId, Func<ResumeLedgerEntry> build, bool newRun = false) {
        try {
            var ledger = StoreFor(agentId)!;
            var result = ledger.TryUpdateUnderLock(agentId, current => newRun ? build() : build() with {
                EndCommittedAt      = current.EndCommittedAt,
                EndCommittedEpoch   = current.EndCommittedEpoch,
                EndCommittedSession = current.EndCommittedSession
            }, EntryLockWait);

            if (result == LockedUpdate.NoEntry) ledger.Write(build());
            else if (result == LockedUpdate.LockTimeout) LogResumeLedgerFailed(new IOException("the entry stayed locked"), "writing", agentId);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            LogResumeLedgerFailed(ex, "writing", agentId);
        }
    }

    ResumeLedgerEntry? TryReadLedger(string agentId) {
        try {
            return StoreFor(agentId)?.TryRead(agentId);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            LogResumeLedgerFailed(ex, "reading", agentId);
            return null;
        }
    }

    /// <summary>What a resume launch carries past an ordinary admitted launch.</summary>
    internal sealed record ResumeLaunch(ResumeLedgerEntry Entry, WorktreeInfo Worktree) {
        /// <summary>The instance the launch built, once it exists.</summary>
        public AgentInstance? Agent { get; set; }

        /// <summary>The ledger entry whose resume this launch is: a withdrawal of it stops the launch.</summary>
        public string AttemptOf { get; init; } = Entry.AgentId;

        /// <summary>A continuation writes its entry only once it takes the worktree over.</summary>
        public bool DefersLedger { get; init; }

        /// <summary>Ends the runtime start with the attempt: a stop during the handshake must not wait for the vendor.</summary>
        public CancellationToken Token { get; init; }

        /// <summary>The agent was waiting when its predecessor stopped it and resumes with no turn: it is published
        /// waiting, before anything can deliver it input.</summary>
        public bool StartsWaiting { get; init; }

        /// <summary>The server's reason, empty when it gave none, once it refused the registration.</summary>
        public string? RefusedRegistration { get; set; }

        /// <summary>Why the account the entry recorded can no longer be used, once the launch refused it.</summary>
        public string? AccountRefusal { get; set; }

        public WorkLocation Work      => Entry.Launch.Work;
        public bool         IsPrivate => Entry.Launch.IsPrivate;
        public string       Origin    => Entry.Launch.Origin;
    }

    const string NativeResumeUnsupported   = "native_resume_unsupported";
    const string StoppedDuringResume       = "stopped";
    const string TranscriptRefusedReason   = "acp_binding_unavailable";
    const string RegistrationRefusedReason = "registration_refused";

    static bool TranscriptRefused(AgentInstance agent) => agent.ReopenedTranscript == ReopenedTranscript.Refused;

    static readonly TimeSpan ReadinessPoll = TimeSpan.FromMilliseconds(50);

    /// How long an interrupted entry waits for `kcap agent resume` or `stop` before a start reaps it.
    static readonly TimeSpan InterruptedRetention = TimeSpan.FromDays(30);

    readonly ConcurrentDictionary<string, ResumeLedgerEntry> _resumeCandidates = new(StringComparer.Ordinal);

    /// <summary>Guarded by <see cref="_ledgerLock"/>.</summary>
    readonly Dictionary<string, ResumeAttempt> _resumeAttempts = new(StringComparer.Ordinal);
    readonly List<ResumeLedgerEntry> _resumeOrder = [];
    readonly List<(ResumeLedgerEntry Entry, string Reason)> _interruptedAtBoot = [];
    string[] _ledgerWorktrees = [];
    string[] _ledgerAgentIds  = [];

    SemaphoreSlim? _resumeLane;

    SemaphoreSlim ResumeLane =>
        _resumeLane ?? Interlocked.CompareExchange(ref _resumeLane, new SemaphoreSlim(Math.Max(1, _config.ResumeConcurrency)), null) ?? _resumeLane!;

    /// <summary>The kcap continue fallback, tried when a native resume is unavailable or fails.</summary>
    internal Func<ResumeLedgerEntry, string, CancellationToken, Task<ResumeOutcome>> ContinueEntry {
        get => field ??= ContinueEntryAsync;
        set;
    }

    /// <summary>Sorts the predecessor's ledger into candidates and entries interrupted at boot. Runs
    /// after the orphan reaper, whose unresolved records it reads, and before the server connect that
    /// claims the candidates. False when the ledger could not be read: the keep-sets are then empty, and
    /// a sweep against them would delete what the ledger protects.</summary>
    internal bool LoadResumeLedger() {
        _startedAt = _time.GetTimestamp();

        if (ResumeLedger is not { } ledger) return true;

        IReadOnlyList<ResumeLedgerEntry> entries;

        try {
            entries = ledger.ReadAll();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            LogResumeLedgerUnreadable(ex);
            return false;
        }

        SortResumeLedger(entries);

        return SortParticipants();
    }

    /// <summary>Planned participant stops of the boot just before this one go ahead of every interactive
    /// candidate; the rest are failed once the lane starts. False when the store could not be read, with
    /// the same meaning as for the interactive ledger.</summary>
    bool SortParticipants() {
        if (ParticipantLedger is not { } ledger) return true;

        IReadOnlyList<ResumeLedgerEntry> entries;

        try {
            entries = ledger.ReadAll();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            LogResumeLedgerUnreadable(ex);
            return false;
        }

        var now   = _time.GetUtcNow();
        var queue = new List<ResumeLedgerEntry>();

        foreach (var entry in entries) {
            _participantEntries[entry.AgentId] = 0;

            if (IsTombstone(entry)) continue;

            if (ParticipantIneligibility(entry, _config.PriorInstanceId, now, _config.AutoResume) is { } reason) {
                _participantFailuresAtBoot.Add((entry, reason));
            } else {
                _resumeCandidates[entry.AgentId] = entry;
                queue.Add(entry);
            }
        }

        _resumeOrder.InsertRange(0, queue);
        _ledgerWorktrees = [.. _ledgerWorktrees, .. entries.Where(e => e.Launch.Work == WorkLocation.OwnedWorktree).Select(e => e.Launch.WorktreePath)];
        _ledgerAgentIds  = [.. _ledgerAgentIds.Concat(entries.Select(e => e.AgentId)).Distinct(StringComparer.Ordinal)];

        return true;
    }

    /// <summary>How long a participant's planned stop stays resumable: the server keeps its claim this long.</summary>
    static readonly TimeSpan ParticipantClaimLifetime = TimeSpan.FromMinutes(15);

    const string TombstonePrefix = "tombstone:";

    static bool IsTombstone(ResumeLedgerEntry entry) =>
        entry.State == ResumeState.Interrupted && entry.LastError?.StartsWith(TombstonePrefix, StringComparison.Ordinal) == true;

    /// <summary>Whether boot will resume this participant under its own id. Read before the ledger is sorted.</summary>
    bool ParticipantResumes(string agentId) {
        try {
            return ParticipantLedger?.TryRead(agentId) is { } entry
                && ParticipantIneligibility(entry, _config.PriorInstanceId, _time.GetUtcNow(), _config.AutoResume) is null;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            LogResumeLedgerFailed(ex, "reading", agentId);
            return false;
        }
    }

    /// <summary>Why a participant entry must not be resumed, or null when it may.</summary>
    internal static string? ParticipantIneligibility(ResumeLedgerEntry entry, string? priorInstanceId, DateTimeOffset now, bool autoResume) =>
        !autoResume                                                                       ? "auto_resume_off"
      : entry.State != ResumeState.Resuming || entry.ResumingAt is null                   ? "not_planned"
      : now - entry.ResumingAt.Value > ParticipantClaimLifetime                           ? "stale"
      : priorInstanceId is null || !string.Equals(entry.PredecessorInstanceId, priorInstanceId, StringComparison.Ordinal) ? "not_predecessor"
      : !Directory.Exists(entry.Launch.WorktreePath)                                      ? "worktree_missing"
      : entry.VendorSession is null                                                       ? "session_missing"
      :                                                                                     null;

    readonly List<(ResumeLedgerEntry Entry, string Reason)> _participantFailuresAtBoot = [];

    /// <summary>Guarded by <see cref="_ledgerLock"/>.</summary>
    readonly Dictionary<string, HeldParticipantInput> _heldInput = new(StringComparer.Ordinal);

    /// <summary>How long after start the server is kept from proving old participants absent while this
    /// daemon resumes its own; every participant still unpublished then is failed.</summary>
    internal TimeSpan ParticipantRecoveryDeadline { get; set; } = TimeSpan.FromSeconds(90);

    /// <summary>When the ledger was loaded, which is daemon start for the deadline's purposes.</summary>
    long _startedAt;

    bool ParticipantRecoveryDeadlinePassed => _time.GetElapsedTime(_startedAt) >= ParticipantRecoveryDeadline;

    /// <summary>A participant candidate not yet published: the server's absence proof ignores resume
    /// claims, so it would record that participant dead.</summary>
    bool ParticipantRecoveryPending() =>
        !ParticipantRecoveryDeadlinePassed
     && _resumeCandidates.Values.Any(e => e.Participant is not null && !_agents.ContainsKey(e.AgentId));

    IReadOnlyList<string> PendingParticipantIds() =>
        [.. _resumeCandidates.Values.Where(e => e.Participant is not null).Select(e => e.AgentId)];

    /// <summary>Withdraws a participant still resuming, as a stop of it does. True once it can no longer
    /// publish.</summary>
    async Task<bool> WithdrawPendingAsync(string agentId) {
        var discard = DiscardResumeEntry(agentId);

        if (discard.Live is { } late) return await StopAgentCoreAsync(late);
        if (discard is { LeavesResourcesToStop: true, Entry: { } entry }) await RemoveDiscardedResourcesAsync(entry);

        return discard.Discarded;
    }

    async Task ObserveDeadlineAsync(CancellationToken ct) {
        try {
            await EnforceParticipantDeadlineAsync(ct);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
        } catch (Exception ex) {
            LogResumeLaneFailed(ex);
        }
    }

    async Task EnforceParticipantDeadlineAsync(CancellationToken ct) {
        var left = ParticipantRecoveryDeadline - _time.GetElapsedTime(_startedAt);
        if (left > TimeSpan.Zero) await Task.Delay(left, _time, ct);

        foreach (var entry in _resumeCandidates.Values.Where(e => e.Participant is not null).ToList()) {
            var discard = DiscardResumeEntry(entry.AgentId);

            // Published, but its registration or readiness has not settled: a candidate is removed once it does.
            if (discard.Live is { } late) {
                WithdrawAttempt(entry.AgentId);
                await DropHeldInputAsync(entry.AgentId);
                await StopAgentCoreAsync(late);
                // After the stop, whose Completed status would clear the failure on the server.
                if (!entry.Launch.IsPrivate) await ReportResumeFailedAsync(entry, "recovery_deadline", null, ct);
                continue;
            }

            if (!discard.Discarded) continue;

            if (!entry.Launch.IsPrivate) await ReportResumeFailedAsync(entry, "recovery_deadline", null, ct);
            if (discard.LeavesResourcesToStop) await RemoveDiscardedResourcesAsync(entry);
        }

        _statusNotifier.Pulse();
    }

    void WithdrawAttempt(string agentId) {
        ResumeAttempt? attempt;

        lock (_ledgerLock) {
            if (!_resumeAttempts.TryGetValue(agentId, out attempt)) return;
            attempt.Withdrawn = true;
        }

        attempt.Cancel();
    }

    /// <summary>True when the input was held for a participant still resuming, false when its queue is
    /// full, null when it is not for one: it is delivered, or dropped, as any other input. Once a queue
    /// exists every later input joins it, so release keeps arrival order.</summary>
    bool? TryHoldForResume(SendInputCommand cmd) {
        lock (_ledgerLock) {
            if (!_heldInput.TryGetValue(cmd.AgentId, out var held)) {
                if (_agents.ContainsKey(cmd.AgentId)
                 || !_resumeCandidates.TryGetValue(cmd.AgentId, out var entry) || entry.Participant is null) return null;

                _heldInput[cmd.AgentId] = held = new HeldParticipantInput();
            }

            return held.TryHold(cmd);
        }
    }

    /// <summary>Delivers what was held for a participant that has resumed, oldest first.</summary>
    async Task ReleaseHeldInputAsync(string agentId) {
        while (true) {
            SendInputCommand next;

            lock (_ledgerLock) {
                if (!_heldInput.TryGetValue(agentId, out var held)) return;

                if (!held.TryTake(out next)) {
                    _heldInput.Remove(agentId);
                    return;
                }
            }

            await DeliverSendInputAsync(next);
        }
    }

    /// <summary>A participant whose resume ended unpublished is gone, which is what each held input is told.</summary>
    async Task DropHeldInputAsync(string agentId) {
        List<SendInputCommand> dropped = [];

        lock (_ledgerLock) {
            if (!_heldInput.Remove(agentId, out var held)) return;

            while (held.TryTake(out var cmd)) dropped.Add(cmd);
        }

        foreach (var cmd in dropped) await ReportInputDroppedAsync(cmd, SendInputDropReason.UnknownAgent);
    }

    /// <summary>The one end of a participant that will not be resumed: reported failed so the server
    /// records its death and the flow heals, then removed — or, while a process of it may still be running,
    /// kept as a tombstone that holds its worktree until the reaper resolves it.</summary>
    async Task FailParticipantAsync(ResumeLedgerEntry entry, string reason, int? exitCode, CancellationToken ct) {
        LogParticipantNotResumed(entry.AgentId, LastErrorOf(reason, exitCode));
        await DropHeldInputAsync(entry.AgentId);

        if (!entry.Launch.IsPrivate) await ReportResumeFailedAsync(entry, reason, exitCode, ct);

        if (IsUnresolved(entry.AgentId)) {
            lock (_ledgerLock) {
                TryWriteLedger(entry.AgentId, () => entry with {
                    State = ResumeState.Interrupted, LastError = TombstonePrefix + reason, UpdatedAt = _time.GetUtcNow()
                });
            }

            return;
        }

        DeleteLedger(entry.AgentId);
        await RemoveDiscardedResourcesAsync(entry);
    }

    bool IsUnresolved(string agentId) =>
        _agents.ContainsKey(agentId) || _orphanReaper?.BlockedCandidates().Any(c => c.AgentId == agentId) == true;

    int _sweepingTombstones;

    /// <summary>Removes each tombstone whose last process is now proven gone. Single-flight; never throws.</summary>
    internal async Task SweepParticipantTombstonesAsync() {
        if (ParticipantLedger is not { } ledger || Interlocked.Exchange(ref _sweepingTombstones, 1) == 1) return;

        try {
            foreach (var entry in ledger.ReadAll().Where(IsTombstone)) {
                if (IsUnresolved(entry.AgentId)) continue;

                DeleteLedger(entry.AgentId);
                await RemoveDiscardedResourcesAsync(entry);
            }
        } catch (Exception ex) {
            LogCleanupStepFailed(ex, "sweeping participant tombstones", "*");
        } finally {
            Volatile.Write(ref _sweepingTombstones, 0);
        }
    }

    void SortResumeLedger(IReadOnlyList<ResumeLedgerEntry> entries) {
        var blocked = (_orphanReaper?.BlockedCandidates() ?? []).Select(c => c.AgentId).ToHashSet(StringComparer.Ordinal);
        var beat    = DaemonHeartbeat.LastBeat(_config.Store, _config.Name);
        var now     = _time.GetUtcNow();

        foreach (var stale in entries.Where(e => e.State == ResumeState.Interrupted && now - e.UpdatedAt > InterruptedRetention))
            DeleteLedger(stale.AgentId);

        entries = [.. entries.Where(e => e.State != ResumeState.Interrupted || now - e.UpdatedAt <= InterruptedRetention)];

        _ledgerWorktrees = [.. entries.Where(e => e.Launch.Work == WorkLocation.OwnedWorktree).Select(e => e.Launch.WorktreePath)];
        _ledgerAgentIds  = [.. entries.SelectMany(e => (e.InheritedAgentIds ?? []).Append(e.AgentId)).Distinct(StringComparer.Ordinal)];

        foreach (var entry in entries) {
            if (entry.State == ResumeState.Interrupted) continue;

            var reason = !_config.AutoResume                                 ? "auto_resume_off"
                       : _config.ResumeMaxAge > TimeSpan.Zero && now - LastAlive(entry, beat) > _config.ResumeMaxAge ? "too_old"
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

    /// <summary>The latest of the entry's write, its resuming stamp and the last heartbeat. A heartbeat file
    /// outlives the incarnation that wrote it, so it never hides a newer write.</summary>
    static DateTimeOffset LastAlive(ResumeLedgerEntry entry, DateTimeOffset? beat) {
        var alive = entry.UpdatedAt;
        if (entry.ResumingAt is { } resuming && resuming > alive) alive = resuming;
        if (beat is { } b && b > alive) alive = b;

        return alive;
    }

    /// <summary>The candidates still waiting on their resume, private agents excluded.</summary>
    internal ResumingAgentClaim[] ResumingClaims() => [
        .. _resumeCandidates.Values
            .Where(e => !e.Launch.IsPrivate)
            .Select(e => new ResumingAgentClaim(e.AgentId, e.PredecessorInstanceId ?? e.DaemonInstanceId ?? "", e.SessionId))
    ];

    /// <summary>Ledger entries with no live agent, as <c>kcap agent ls</c> shows them: the candidates the
    /// resume lane still holds, then the interrupted entries with their reason. <paramref name="includeLive"/>
    /// keeps the entries of agents live right now, for a caller reconciling against its own live observation.</summary>
    internal IReadOnlyList<(ResumeLedgerEntry Entry, string Status, string State)> LedgerOnlyAgents(bool includeLive = false) {
        // Under the lock a retry takes to write its entry and add its candidate, so no entry is caught
        // between the two and missing from both.
        lock (_ledgerLock) return LedgerOnlyAgentsLocked(includeLive);
    }

    IReadOnlyList<(ResumeLedgerEntry Entry, string Status, string State)> LedgerOnlyAgentsLocked(bool includeLive) {
        var rows = _resumeCandidates.Values
            .Where(e => includeLive || !_agents.ContainsKey(e.AgentId))
            .OrderBy(e => e.CreatedAt)
            .Select(e => (e, "Resuming", "resuming"))
            .ToList();

        IReadOnlyList<ResumeLedgerEntry> entries;

        try {
            entries = ResumeLedger?.ReadAll() ?? [];
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            LogResumeLedgerUnreadable(ex);
            return rows;
        }

        rows.AddRange(entries
            .Where(e => e.State == ResumeState.Interrupted && (includeLive || !_agents.ContainsKey(e.AgentId)) && !_resumeCandidates.ContainsKey(e.AgentId))
            .OrderBy(e => e.CreatedAt)
            .Select(e => (e, "Interrupted", e.LastError is { Length: > 0 } reason ? $"interrupted ({reason})" : "interrupted")));

        return rows;
    }

    /// <summary>What <see cref="DiscardResumeEntry"/> did. <see cref="Entry"/> is the discarded entry, null
    /// when nothing was; <see cref="Live"/> is the agent published under the id when that is why, so a
    /// caller that missed it a moment earlier can stop it as live.</summary>
    internal readonly record struct ResumeDiscard(ResumeLedgerEntry? Entry, bool AttemptInFlight, AgentInstance? Live) {
        public bool Discarded => Entry is not null;

        /// <summary>The stop releases the entry's worktree and attachments itself; an attempt in flight
        /// releases them once it has ended.</summary>
        public bool LeavesResourcesToStop => Entry is not null && !AttemptInFlight;
    }

    /// <summary>`kcap agent stop` of an agent the daemon is not running: deletes its entry and ring and
    /// withdraws it from the resume lane. False when there is a live agent or no entry.</summary>
    internal bool TryDiscardResumeEntry(string agentId) => DiscardResumeEntry(agentId).Discarded;

    internal ResumeDiscard DiscardResumeEntry(string agentId) {
        if (StoreFor(agentId) is not { } ledger) return default;

        ResumeAttempt?     attempt;
        ResumeLedgerEntry? entry;

        // Under the lock a resume publishes under: either the agent is live and its own stop applies,
        // or the attempt is withdrawn before it can publish.
        lock (_ledgerLock) {
            if (_agents.TryGetValue(agentId, out var live)) return new ResumeDiscard(null, false, live);
            if ((entry = TryReadLedger(agentId)) is null) return default;

            try {
                ledger.Delete(agentId);
                _participantEntries.TryRemove(agentId, out _);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                LogResumeLedgerFailed(ex, "deleting", agentId);
                return default;
            }

            _resumeCandidates.TryRemove(agentId, out _);

            if (_resumeAttempts.TryGetValue(agentId, out attempt)) {
                attempt.Withdrawn = true;
                attempt.Discarded = true;
            }
        }

        attempt?.Cancel();
        _statusNotifier.Pulse();

        return new ResumeDiscard(entry, attempt is not null, null);
    }

    /// <summary>The owned worktree and attachment directory of a discarded entry, as a normal stop
    /// removes them. A borrowed or external path is never touched. Never throws.</summary>
    async Task RemoveDiscardedResourcesAsync(ResumeLedgerEntry entry) {
        if (entry.Launch.Work == WorkLocation.OwnedWorktree) {
            try {
                await RemoveOwnedWorktreeAsync(ResumedWorktree(entry), entry.AgentId);
            } catch (Exception ex) {
                LogCleanupStepFailed(ex, "removing a discarded resume's worktree", entry.AgentId);
            }
        }

        try {
            _attachmentStore.Remove(entry.AgentId);
        } catch (Exception ex) {
            LogCleanupStepFailed(ex, "removing a discarded resume's attachments", entry.AgentId);
        }
    }

    bool IsWithdrawn(string agentId) {
        lock (_ledgerLock) return _resumeAttempts.TryGetValue(agentId, out var attempt) && attempt.Withdrawn;
    }

    /// <summary>Publishes a resumed agent and rewrites its entry unless its attempt was withdrawn, in
    /// one step under the ledger lock a withdrawal takes.</summary>
    bool TryPublishResumed(AgentInstance agent, ResumeLaunch resume, CapacityReservation capacity) {
        lock (_ledgerLock) {
            if (IsWithdrawn(resume.AttemptOf)) return false;

            resume.Agent = agent;
            ReplayPersistedRing(agent);
            if (resume.StartsWaiting) agent.ActivityClock.SetAwaitingInput(true);
            capacity.Publish(agent);
            if (!resume.DefersLedger) WriteResumedLedger(agent, resume.Entry);

            return true;
        }
    }

    /// <summary>Ends the runtime a withdrawn resume started before anything else learned of it.</summary>
    async Task AbandonWithdrawnResumeAsync(AgentInstance agent, TranscriptJournal? journal) {
        try {
            await agent.Runtime.TerminateAsync(ShutdownTerminateGrace);
            await agent.Runtime.DisposeAsync();
        } catch (Exception ex) {
            LogCleanupStepFailed(ex, "terminating a withdrawn resume", agent.Id);
        }

        agent.ReadCts.Dispose();
        await DiscardFailedLaunchJournalAsync(journal, agent.Id);
    }

    /// <summary>Every owned worktree the ledger names, interrupted entries included: a later manual
    /// resume needs its worktree as much as a boot candidate does.</summary>
    internal IReadOnlyList<string> LedgerWorktreePaths() => _ledgerWorktrees;

    /// <summary>The boot resume lane: reports the entries interrupted at boot, then resumes the
    /// candidates at most <see cref="DaemonConfig.ResumeConcurrency"/> at a time. Never throws.</summary>
    internal async Task ResumeOnBootAsync(CancellationToken ct) {
        try {
            foreach (var (entry, reason) in _interruptedAtBoot.Where(i => !i.Entry.Launch.IsPrivate))
                await ReportResumeFailedAsync(entry, reason, null, ct);

            foreach (var (entry, reason) in _participantFailuresAtBoot)
                await FailParticipantAsync(entry, reason, null, ct);

            if (_resumeCandidates.Values.Any(e => e.Participant is not null)) _ = ObserveDeadlineAsync(ct);

            await Task.WhenAll(_resumeOrder.Select(entry => RunResumeThroughLaneAsync(entry, ct)));
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
        } catch (Exception ex) {
            LogResumeLaneFailed(ex);
        }
    }

    async Task RunResumeThroughLaneAsync(ResumeLedgerEntry entry, CancellationToken ct) {
        await ResumeLane.WaitAsync(ct);

        ResumeAttempt? attempt = null;

        try {
            lock (_ledgerLock) {
                // A stop while the entry waited for the lane withdrew it.
                if (!_resumeCandidates.ContainsKey(entry.AgentId)) return;

                attempt = new ResumeAttempt(ct);
                _resumeAttempts[entry.AgentId] = attempt;
            }

            await ResumeEntryAsync(entry, attempt.Token);
        } catch (OperationCanceledException) when (attempt is not null && IsWithdrawn(entry.AgentId) && !ct.IsCancellationRequested) {
            // The withdrawal already removed the entry.
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            LogResumeFailed(ex, entry.AgentId);

            // Left resuming with no candidate, the entry would be invisible to `agent ls` and refused by
            // `agent resume` until the next boot.
            if (!_agents.ContainsKey(entry.AgentId) && !IsWithdrawn(entry.AgentId)) {
                if (entry.Participant is not null) await FailParticipantAsync(entry, ex.GetType().Name, null, CancellationToken.None);
                else InterruptEntry(entry.AgentId, ex.GetType().Name);
            }
        } finally {
            bool discarded;

            // A manual retry may already hold the id with its own candidate and attempt.
            lock (_ledgerLock) {
                if (attempt is not null && _resumeAttempts.TryGetValue(entry.AgentId, out var registered) && ReferenceEquals(registered, attempt))
                    _resumeAttempts.Remove(entry.AgentId);

                if (_resumeCandidates.TryGetValue(entry.AgentId, out var candidate) && ReferenceEquals(candidate, entry))
                    _resumeCandidates.TryRemove(entry.AgentId, out _);

                discarded = attempt?.Discarded == true;
            }

            _statusNotifier.Pulse();

            if (!_agents.ContainsKey(entry.AgentId)) await DropHeldInputAsync(entry.AgentId);

            // Whatever the attempt had started is ended by now, so nothing still runs in the worktree.
            if (discarded) await RemoveDiscardedResourcesAsync(entry);

            attempt?.Dispose();
            ResumeLane.Release();
        }
    }

    /// <summary>An operator's retry of an interrupted entry: <c>resuming</c> once the entry is marked
    /// resuming and queued on the boot lane's gate, <c>not_interrupted</c> for any other state,
    /// <c>unknown</c> without an entry, and <c>blocked:&lt;reason&gt;</c> while the predecessor's process is
    /// unresolved or the entry was written by a newer daemon. A ledger that cannot be written throws and
    /// nothing is queued. The age cap and <see cref="DaemonConfig.AutoResume"/> do not apply to an explicit
    /// retry.</summary>
    internal Task<string> ResumeInterruptedAsync(string agentId, CancellationToken ct) {
        ResumeLedgerEntry resuming;

        lock (_ledgerLock) {
            if (TryReadLedger(agentId) is not { } entry) return Task.FromResult("unknown");
            // A participant's flow has healed without it by now; resuming it would add a second reviewer.
            if (entry.State != ResumeState.Interrupted || entry.Participant is not null) return Task.FromResult("not_interrupted");
            if (ResumeBlockedReason(entry) is { } blocked) return Task.FromResult($"blocked:{blocked}");

            var now = _time.GetUtcNow();
            resuming = entry with { State = ResumeState.Resuming, ResumingAt = now, LastError = null, UpdatedAt = now };

            ResumeLedger!.Write(resuming);

            _resumeCandidates[agentId] = resuming;
        }

        _statusNotifier.Pulse();
        _ = RunResumeThroughLaneAsync(resuming, _shutdownCts.Token);

        return Task.FromResult("resuming");
    }

    /// <summary>Why a retry must not run: a second process beside one the reaper could not prove gone, or a
    /// rewrite of an entry whose schema this daemon does not know.</summary>
    string? ResumeBlockedReason(ResumeLedgerEntry entry) =>
        entry.LastError == ResumeLedgerStore.NewerSchemaReason ? ResumeLedgerStore.NewerSchemaReason
      : _orphanReaper?.BlockedCandidates().Any(c => c.AgentId == entry.AgentId) == true ? "predecessor_process_unresolved"
      : null;

    /// <summary>Native resume, then kcap continue, then interrupted.</summary>
    internal async Task<ResumeOutcome> ResumeEntryAsync(ResumeLedgerEntry entry, CancellationToken ct) {
        var participant = entry.Participant is not null;
        var native = entry.VendorSession is not null
                  && _runtimeFactories.TryGetValue(entry.Vendor, out var factory)
                  && (participant ? factory.SupportsReviewerRestartResume(entry.Transport, entry.Launch.Work) : factory.SupportsRestartResume(entry.Transport));

        string reason;
        int?   exitCode = null;

        if (native) {
            if (await NativeResumeAsync(entry, ct) is not { } failure) return ResumeOutcome.Resumed;

            (reason, exitCode) = failure;

            // The stop's own teardown ends the agent and removes what it kept.
            if (reason == StoppedDuringResume) return ResumeOutcome.Stopped;

            // A start the shutdown or a withdrawal cancelled is no failed attempt.
            ct.ThrowIfCancellationRequested();

            if (participant) {
                if (IsWithdrawn(entry.AgentId)) return ResumeOutcome.Stopped;

                await FailParticipantAsync(entry, reason, exitCode, ct);

                return ResumeOutcome.Interrupted;
            }

            if (RecordFailedAttempt(entry, LastErrorOf(reason, exitCode)) is not { } failed) return ResumeOutcome.Stopped;

            entry = failed;
        } else if (ResolveAccount(entry.Vendor, accountId: null, entry.Launch).Refusal is { } refusal) {
            reason = refusal;

            if (RecordFailedAttempt(entry, reason) is not { } failed) return ResumeOutcome.Stopped;

            entry = failed;
        } else {
            reason = NativeResumeUnsupported;
        }

        // A participant continued under a new id would be an agent its flow does not know.
        if (participant) {
            await FailParticipantAsync(entry, reason, exitCode, ct);

            return ResumeOutcome.Interrupted;
        }

        // A continuation resolves the same recorded account, so it would be refused too.
        if (!reason.StartsWith(AccountResolution.Unavailable, StringComparison.Ordinal)) {
            var outcome = await ContinueEntry(entry, reason, ct);
            if (outcome != ResumeOutcome.Interrupted) return outcome;
            if (IsWithdrawn(entry.AgentId)) return ResumeOutcome.Stopped;
        }

        InterruptEntry(entry.AgentId, LastErrorOf(reason, exitCode));
        if (!entry.Launch.IsPrivate && reason != RegistrationRefusedReason) await ReportResumeFailedAsync(entry, reason, exitCode, ct);

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
            entry.Launch.RequesterUserId, RequesterIsOwner: false, LaunchConsentEngine.KindToken(entry.Participant?.Kind ?? LaunchKind.Default),
            entry.Launch.RepoPath, entry.Vendor, entry.Launch.RequesterDisplay);
        var consent = _consentGate.GrantResume(entry.AgentId, input, entry.Launch.ConsentSource ?? "");

        // An agent that was waiting comes back waiting: a nudge would start a turn nobody asked for. A participant
        // always gets its nudge, which is how it learns it was relaunched mid-flow.
        var silent  = entry.Participant is null && entry.AwaitingInput == true
                   && _runtimeFactories.TryGetValue(entry.Vendor, out var factory) && factory.ResumesIdleWithoutPrompt(entry.Transport);
        var resume  = new ResumeLaunch(entry, ResumedWorktree(entry)) { Token = ct, StartsWaiting = silent };
        var command = BuildResumeCommand(entry, LastAlive(entry, null), _server.CurrentConnectionId) is var built && silent
            ? built with { Prompt = "" }
            : built;
        var outcome = await LaunchAdmittedAsync(command, consent, hold, resume, cancel: CancellationToken.None); // the resume's own token ends its start

        if (resume.Agent is null && IsWithdrawn(entry.AgentId)) return (StoppedDuringResume, null);

        if (resume.AccountRefusal is { } refusal) return (refusal, null);

        // The server holds no live entry under this id, so the resume goes on under a new one, unless a stop
        // came first: it gave up the worktree, and the teardown has removed it.
        if (resume.RefusedRegistration is not null)
            return resume.Agent?.StopRequested == true ? (StoppedDuringResume, null) : (RegistrationRefusedReason, null);

        if (outcome.Kind == CommandOutcomeKind.LaunchRejected)
            return (outcome.RejectReason == CommandRejectedReason.DaemonCapacity ? "daemon_capacity" : "launch_rejected", null);

        if (resume.Agent is not { } agent) return ("runtime_exited", null);

        var survived = outcome.Kind == CommandOutcomeKind.LaunchExecuted && await SurvivesReadinessAsync(agent, ct, judgeTranscript: true);

        if (agent.StopRequested) return (StoppedDuringResume, null);

        if (!survived && TranscriptRefused(agent)) {
            // Its first turn was never sent, so kcap continue repeats nothing; still pending its resume,
            // the teardown this starts keeps the worktree for it.
            await agent.Runtime.TerminateAsync(ShutdownTerminateGrace);

            return (TranscriptRefusedReason, null);
        }

        if (!survived) return ("runtime_exited", agent.Runtime.ExitCode);

        lock (_ledgerLock) {
            // A shutdown that began meanwhile has marked the agent for its own resume.
            if (!agent.IsCleanupStarted && !_shutdownCts.IsCancellationRequested) agent.ResumePending = false;
        }

        await ReleaseHeldInputAsync(entry.AgentId);

        return null;
    }

    /// <summary>Whether the resumed runtime is still running, and still this agent, once the readiness
    /// window has passed since it published. <paramref name="judgeTranscript"/> also fails it unless the
    /// server has accepted the session it reopened by then.</summary>
    async Task<bool> SurvivesReadinessAsync(AgentInstance agent, CancellationToken ct, bool judgeTranscript = false) {
        var started = _time.GetTimestamp();

        while (!agent.Runtime.HasExited && _agents.TryGetValue(agent.Id, out var live) && ReferenceEquals(live, agent)
            && !(judgeTranscript && TranscriptRefused(agent))) {
            var left = _config.ResumeReadinessWindow - _time.GetElapsedTime(started);
            if (left <= TimeSpan.Zero) return !(judgeTranscript && agent.TryRefuseReopenedTranscript());

            await Task.Delay(left < ReadinessPoll ? left : ReadinessPoll, _time, ct);
        }

        return false;
    }

    const string SupersededReason = "superseded";

    internal TimeSpan ContinueSummaryBudget { get; set; } = TimeSpan.FromSeconds(10);

    string ServerBase => _config.ServerUrl.TrimEnd('/');

    /// <summary>Starts a fresh agent under a new id in the entry's worktree and, once it has stayed alive
    /// through <see cref="DaemonConfig.ResumeReadinessWindow"/>, hands it the worktree and ends the entry's
    /// agent and session as superseded; the successor's session takes over the old one's work items and
    /// plans once its id is known. Until that handover the entry keeps the worktree.
    /// <see cref="ResumeOutcome.Interrupted"/>, with nothing reported, when the successor does not launch
    /// or does not survive the window: the lane's failed-resume report is then the old agent's only end.
    /// <see cref="ResumeOutcome.Stopped"/>, with the entry left resuming, when a shutdown begins first.</summary>
    internal async Task<ResumeOutcome> ContinueEntryAsync(ResumeLedgerEntry entry, string reason, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();

        var summary = await ReadPreviousSummaryAsync(entry.SessionId, ct);
        var prompt  = ContinuePrompt.Build(entry.Launch.Prompt, entry.SessionId, reason, summary, LastAlive(entry, null));

        var launch   = await LaunchContinuationAsync(entry, Guid.NewGuid().ToString("N"), prompt, ct);
        var agent    = launch?.Agent;
        var survived = false;

        if (agent is not null) {
            try {
                survived = await SurvivesReadinessAsync(agent, ct);
            } catch (OperationCanceledException) when (IsWithdrawn(entry.AgentId)) {
                // Handled with any other withdrawal below.
            }
        }

        if (survived && TryHandOverContinuation(entry, launch!.Entry, agent!)) {
            await ReportSupersededAsync(entry, reason, ct);
            if (entry.SessionId is { } previous) StartContinuationTakeover(agent!, previous);

            return ResumeOutcome.Continued;
        }

        if (IsWithdrawn(entry.AgentId)) {
            if (agent is not null) await StopAgentCoreAsync(agent);

            return ResumeOutcome.Stopped;
        }

        // A start the shutdown cancelled is no failed continuation: the entry stays resumable.
        ct.ThrowIfCancellationRequested();

        if (agent is null) return ResumeOutcome.Interrupted;

        // A stop of the successor itself ends the agent it continued too.
        if (agent.StopRequested) {
            DeleteLedger(entry.AgentId);
            await ReportSupersededAsync(entry, reason, ct);

            return ResumeOutcome.Stopped;
        }

        return _shutdownCts.IsCancellationRequested ? ResumeOutcome.Stopped : ResumeOutcome.Interrupted;
    }

    /// <summary>Gives the worktree to a successor that passed readiness: its own entry is written and
    /// the continued one deleted in one step under the ledger lock, so a stop or a shutdown sees one owner.</summary>
    bool TryHandOverContinuation(ResumeLedgerEntry entry, ResumeLedgerEntry successor, AgentInstance agent) {
        lock (_ledgerLock) {
            if (IsWithdrawn(entry.AgentId) || agent.IsCleanupStarted || agent.StopRequested || _shutdownCts.IsCancellationRequested)
                return false;

            agent.ResumePending = false;
            WriteResumedLedger(agent, successor);
            DeleteLedger(entry.AgentId);

            return true;
        }
    }

    /// <summary>A private agent was never registered, and a refused one has no live entry left on the server:
    /// neither has an agent to end, and a status for the refused id could overwrite the failure it recorded.</summary>
    Task ReportSupersededAsync(ResumeLedgerEntry entry, string reason, CancellationToken ct) =>
        entry.Launch.IsPrivate || reason == RegistrationRefusedReason
            ? Task.CompletedTask
            : _server.ReportAgentEndedForShutdownAsync(entry.AgentId, entry.SessionId, "Completed", SupersededReason, null, ct);

    /// <summary>Runs the takeover off the lane, once, as soon as the successor's session exists and the
    /// session it continues is known: either may come first. A terminal agent's pinned id names no
    /// session until its transcript is found, so its takeover waits for discovery.</summary>
    void StartContinuationTakeover(AgentInstance agent, string? previousSession = null) {
        string previous, current;

        lock (_ledgerLock) {
            if (previousSession is not null) agent.ContinuedFromSessionId = previousSession;
            if (agent.ContinuedFromSessionId is not { } from || agent.SessionId is not { } own) return;
            if (agent.Runtime is not IAcpTranscriptSource && agent.TranscriptPath is null) return;

            agent.ContinuedFromSessionId = null;
            (previous, current) = (from, own);
        }

        _ = TakeOverAsync(agent.Id, previous, current, _shutdownCts.Token);
    }

    /// <summary>The successor's launch once its agent is published, or null when it did not launch. It
    /// keeps its pending resume and writes no entry until the handover, so its teardown leaves the
    /// worktree to the entry it continues.</summary>
    async Task<ResumeLaunch?> LaunchContinuationAsync(ResumeLedgerEntry entry, string agentId, string prompt, CancellationToken ct) {
        using var admission = _admission.TryAdmit();
        if (admission is null) return null;

        using var hold = BeginLaunchHold();

        var input = new LaunchConsentInput(
            entry.Launch.RequesterUserId, RequesterIsOwner: false, LaunchConsentEngine.KindToken(LaunchKind.Default),
            entry.Launch.RepoPath, entry.Vendor, entry.Launch.RequesterDisplay);
        var consent = _consentGate.GrantContinue(agentId, input, entry.Launch.ConsentSource ?? "");

        var successor = entry with {
            AgentId = agentId, SessionId = null, VendorSession = null, Attempts = 0, LastError = null, CreatedAt = _time.GetUtcNow(),
            LaunchEpoch = _daemonEpoch,
            InheritedAgentIds = [.. (entry.InheritedAgentIds ?? []), entry.AgentId],
            // The server admits a repeated hand-off begin only for the session it continued the source as.
            Launch = entry.Launch with { HandoffSource = null }
        };
        var launch  = new ResumeLaunch(successor, ResumedWorktree(entry)) { AttemptOf = entry.AgentId, DefersLedger = true, Token = ct };
        var cmd     = BuildResumeCommand(entry, LastAlive(entry, null), _server.CurrentConnectionId) with { AgentId = agentId, Prompt = prompt, ResumeSessionId = null };
        var outcome = await LaunchAdmittedAsync(cmd, consent, hold, launch, cancel: CancellationToken.None); // the resume's own token ends its start

        return launch.Agent is not null && outcome.Kind == CommandOutcomeKind.LaunchExecuted ? launch : null;
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
                .RunAsync(client, ServerBase, previous, current, TakeoverAdmission.Judged, ct: ct);

            if (result is TakeoverResult.Interrupted && ct.IsCancellationRequested) return;

            LogContinueTakeover(agentId, previous, current, result switch {
                TakeoverResult.Completed { Unsuccessful: false } => "completed",
                TakeoverResult.Completed                         => "partial",
                TakeoverResult.Refused r                         => $"refused: {r.Reason}",
                TakeoverResult.Failed f                          => $"failed: {f.Reason}",
                TakeoverResult.Interrupted i                     => $"failed: interrupted ({i.Code})",
                _                                                => "unauthorized"
            });
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
        } catch (Exception ex) {
            LogContinueTakeoverFailed(ex, agentId, previous);
        }
    }

    /// <summary>A participant relaunches as the reviewer it was, its certification re-checked against this
    /// connection: the predecessor's connection id no longer exists.</summary>
    static LaunchAgentCommand BuildResumeCommand(ResumeLedgerEntry entry, DateTimeOffset interruptedAt, string? connectionId) {
        var command = BuildInteractiveResumeCommand(entry, interruptedAt);

        return entry.Participant is not { } participant ? command : command with {
            Prompt                = ReviewerResumeNudge.Build(interruptedAt, participant.LastRoundInput),
            Kind                  = participant.Kind,
            McpAllowlist          = participant.McpAllowlist,
            Tools                 = participant.Tools,
            CodexPosture          = null,
            // The resume resolves the account the entry recorded; a review-flow launch naming one is refused.
            AccountId             = null,
            ReviewerCertification = participant.Certification is { } certification
                ? certification with { ExpectedDaemonConnectionId = connectionId ?? "" }
                : null
        };
    }

    static LaunchAgentCommand BuildInteractiveResumeCommand(ResumeLedgerEntry entry, DateTimeOffset interruptedAt) {
        var launch = entry.Launch;

        return new LaunchAgentCommand(
            AgentId:             entry.AgentId,
            Prompt:              ResumeNudge.Build(interruptedAt, launch.Prompt),
            Model:               launch.Model ?? "",
            Effort:              launch.Effort,
            // A scratch launch names no repository; the resumed worktree is its workspace.
            RepoPath:            launch.ScratchWorkspace == true ? "" : launch.RepoPath,
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
            InactivityBoundSeconds: launch.InactivityBoundSeconds,
            PermissionMode:      launch.PermissionMode,
            Title:               launch.Title,
            // A prompt cut re-sent as the caller's title would outrank the one the session has since been given.
            TitleDerived:        launch.TitleDerived ?? true,
            AccountId:           launch.AccountId,
            ScratchWorkspace:    launch.ScratchWorkspace == true);
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
            IsStandalone: launch.WorktreeIsStandalone ?? Directory.Exists(Path.Combine(path, ".git")),
            FetchedRef: WorktreeManager.FetchedRefFor(Path.GetFileName(path))) {
            GitRelativeCwd = launch.GitRelativeCwd,
            OwnsBranch     = launch.WorktreeOwnsBranch ?? true,
        };
    }

    /// <summary>Replays the predecessor's persisted ring into a resumed agent, then removes it.</summary>
    void ReplayPersistedRing(AgentInstance agent) {
        var path = StoreFor(agent.Id)!.RingPath(agent.Id);
        if (!File.Exists(path)) return;

        try {
            ReplayRing(agent, File.ReadAllBytes(path));
            File.Delete(path);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            LogResumeRingFailed(ex, agent.Id);
        }
    }

    /// The ledger's started_at, and the stamp an attach acknowledgement reports: both must be this value.
    static DateTimeOffset StartedAtOf(AgentInstance agent) => new(DateTime.SpecifyKind(agent.CreatedAt, DateTimeKind.Utc));

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
                HasTerminal           = agent.Runtime.EmitsTerminalOutput,
                TranscriptFormat      = TranscriptFormatOf(agent),
                StartedAt             = StartedAtOf(agent),
                SessionId             = agent.SessionId ?? entry.SessionId,
                VendorSession         = agent.VendorSession ?? entry.VendorSession,
                State                 = ResumeState.Running,
                LastError             = null,
                PredecessorInstanceId = null,
                ResumingAt            = null,
                RunEpoch              = _daemonEpoch,
                EndCommittedAt        = null,
                EndCommittedEpoch     = null,
                EndCommittedSession   = null,
                // Waiting is a fact about the stop it was recorded at; a crash before the next one proves nothing.
                AwaitingInput         = null
            }, newRun: true);
        }
    }

    /// <summary>Null when the attempt was withdrawn: its entry is gone and must stay gone.</summary>
    ResumeLedgerEntry? RecordFailedAttempt(ResumeLedgerEntry entry, string reason) {
        var failed = entry with { Attempts = entry.Attempts + 1, LastError = reason, UpdatedAt = _time.GetUtcNow() };

        lock (_ledgerLock) {
            if (IsWithdrawn(entry.AgentId)) return null;

            TryWriteLedger(entry.AgentId, () => failed);
        }

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

        _statusNotifier.Pulse();
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Resume: flow participant {AgentId} was not resumed: {Reason}")]
    partial void LogParticipantNotResumed(string agentId, string reason);
}
