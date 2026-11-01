using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Recording;
using Capacitor.Cli.Core.Resume;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

internal partial class AgentOrchestrator {
    internal HarnessRegistry HarnessesForTest => _harnesses;

    /// <summary>Answers whether an ended session can be resumed here. A payload that does not parse is refused
    /// <c>resume_invalid</c> rather than answered with an Error frame.</summary>
    public async Task HandleLocalResumeProbeAsync(string payload, Stream stream, CancellationToken ct) {
        string? refusal;
        try {
            refusal = JsonSerializer.Deserialize(payload, ResumeInPlaceIpcJsonContext.Default.ResumeProbeRequestDto) is { } request
                ? await InPlaceResumeRefusalAsync(request.Vendor, request.SessionId, request.Directory, request.AccountId, request.ConfigDir)
                : ResumeInPlaceRefusals.Invalid;
        } catch (JsonException) {
            refusal = ResumeInPlaceRefusals.Invalid;
        }

        try {
            var json = JsonSerializer.Serialize(new ResumeProbeReplyDto(refusal), ResumeInPlaceIpcJsonContext.Default.ResumeProbeReplyDto);
            await FrameCodec.WriteAsync(stream, new LocalFrame(FrameType.ResumeProbeReply) { Text = json }, ct);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            _logger.LogDebug(ex, "ResumeProbe: the reply could not be written");
        }
    }

    /// <summary>Why the session cannot be resumed natively in <paramref name="directory"/> under the store it was
    /// recorded with, or null when it can. The probe and the launch run this same check.</summary>
    internal async Task<string?> InPlaceResumeRefusalAsync(string vendor, string sessionId, string directory, string? accountId, string? configDir) {
        if (NativeResumeId.For(vendor, sessionId) is null
         || !_runtimeFactories.TryGetValue(vendor.ToLowerInvariant(), out var factory)
         || !factory.SupportsRestartResume(null))
            return ResumeInPlaceRefusals.UnsupportedVendor;

        if (SessionId.Parse(SessionIds.Canonical(sessionId)) is { } session && HostsSession(session)) return ResumeInPlaceRefusals.SourceActive;

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return ResumeInPlaceRefusals.DirectoryMissing;

        if (!(await new BorrowAuthorizer(_config).AuthorizeBorrowAsync(directory)).Allowed) return ResumeInPlaceRefusals.DirectoryNotAllowed;

        return StoreRefusal(vendor, accountId, configDir);
    }

    /// <summary>The recorded store must be the one a resume runs under. An account vendor resolves it the way restart
    /// recovery does, keeping the recorded directory; with no registered account it must still be today's default,
    /// so a changed default is not resumed into. A vendor without accounts must still resolve its root to it.</summary>
    string? StoreRefusal(string vendorToken, string? accountId, string? configDir) {
        if (string.IsNullOrWhiteSpace(configDir) || HarnessId.From(vendorToken) is not { } vendor) return ResumeInPlaceRefusals.StoreChanged;

        try {
            if (!_config.DefaultDirectories.TryGetValue(vendor, out var defaultDirectory))
                return RecordingStore.Of(vendor, null, _harnesses) is { } root && AccountDirectory.Same(root, configDir) && Directory.Exists(configDir)
                    ? null
                    : ResumeInPlaceRefusals.StoreChanged;

            var resolved = AccountResolution.ResolveForResume(_config.Accounts?.TryLoad(), vendor, accountId, configDir, defaultDirectory);

            if (resolved.Account is not { } account || account.Directory != configDir) return ResumeInPlaceRefusals.StoreChanged;

            return account.AccountId is not null || AccountDirectory.Same(defaultDirectory, configDir) ? null : ResumeInPlaceRefusals.StoreChanged;
        } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) {
            return ResumeInPlaceRefusals.StoreChanged;
        }
    }
}

internal partial class AgentOrchestrator {
    internal TimeSpan SourceLeaseRenewal { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>The checks a resume launch runs before consent: a block the daemon cannot carry, and every reason the
    /// probe would have given.</summary>
    async Task<string?> InPlaceLaunchRefusalAsync(LaunchAgentCommand cmd, ResumeInPlaceLaunch inPlace) {
        if (cmd.Kind != LaunchKind.Default || cmd.Borrowed || cmd.Handoff is not null || cmd.ScratchWorkspace
         || !string.IsNullOrEmpty(cmd.ResumeSessionId) || string.IsNullOrWhiteSpace(cmd.Vendor)
         || string.IsNullOrWhiteSpace(inPlace.SourceSessionId) || string.IsNullOrWhiteSpace(inPlace.ReservationToken))
            return ResumeInPlaceRefusals.Invalid;

        return await InPlaceResumeRefusalAsync(cmd.Vendor, inPlace.SourceSessionId, inPlace.Directory, inPlace.AccountId, inPlace.ConfigDir);
    }

    /// <summary>
    /// Resumes the source natively in its own directory through the restart-resume protocol: the runtime is bound to
    /// the original session from the start and judged by the readiness window, transcript included. A failure after
    /// the agent published retires it before <c>LaunchFailed</c> goes out, so the app's fallback cannot race it.
    /// </summary>
    async Task<CommandOutcome> LaunchInPlaceAsync(
            LaunchAgentCommand cmd, ResumeInPlaceLaunch inPlace, LaunchConsentOutcome consent, LaunchHold hold,
            CancellationToken cancel, Action<AgentInstance>? onPublished) {
        var factory = _runtimeFactories[cmd.Vendor];
        var native  = NativeResumeId.For(cmd.Vendor, inPlace.SourceSessionId)!;
        var entry   = InPlaceEntry(cmd, inPlace, native, consent);
        var resume  = new ResumeLaunch(entry, WorktreeInfo.Borrowed(inPlace.Directory)) {
            Token         = cancel,
            StartsWaiting = string.IsNullOrWhiteSpace(cmd.Prompt) && factory.ResumesIdleWithoutPrompt(entry.Transport),
        };
        var command = cmd with { RepoPath = inPlace.Directory, ResumeSessionId = native, ResumeInPlace = null, Prompt = cmd.Prompt ?? "" };

        var outcome = await LaunchAdmittedAsync(command, consent, hold, resume, cancel, onPublished);

        // Every failure before this point that reached LaunchAdmittedAsync's own paths was reported there.
        if (resume.RefusedRegistration is not null) {
            await _server.LaunchFailedAsync(cmd.AgentId, $"{ResumeInPlaceRefusals.Failed}: {RegistrationRefusedReason}");

            return outcome;
        }

        if (resume.Agent is not { } agent) return outcome;

        // A launch that published and then failed, such as a process that exited at once, was reported there too.
        if (outcome.Kind != CommandOutcomeKind.LaunchExecuted) {
            lock (_ledgerLock) {
                agent.ResumePending = false;
                DeleteLedger(cmd.AgentId);
            }

            return outcome;
        }

        var survived = await SurvivesReadinessAsync(agent, _shutdownCts.Token, judgeTranscript: true);

        // A stop that came first ends the agent through its own teardown.
        if (agent.StopRequested) return outcome;

        if (survived) {
            lock (_ledgerLock) {
                if (!agent.IsCleanupStarted && !_shutdownCts.IsCancellationRequested) agent.ResumePending = false;
            }

            return outcome;
        }

        var reason = TranscriptRefused(agent) ? TranscriptRefusedReason : "runtime_exited";

        LogInPlaceResumeFailed(cmd.AgentId, inPlace.SourceSessionId, reason);

        // Claimed before the retirement, so the exit's own verdict does not report the same failure a second time.
        var reports = Interlocked.CompareExchange(ref agent.LaunchFailureVerdictReported, 1, 0) == 0;

        // Nothing is kept for a later resume: the app falls back to a hand-off, and the directory was never the daemon's.
        lock (_ledgerLock) agent.ResumePending = false;
        agent.StopDetail ??= $"{ResumeInPlaceRefusals.Failed}: {reason}";
        await RetireAsync(agent);
        lock (_ledgerLock) DeleteLedger(cmd.AgentId);
        if (reports) await _server.LaunchFailedAsync(cmd.AgentId, $"{ResumeInPlaceRefusals.Failed}: {reason}");

        return new CommandOutcome(CommandOutcomeKind.LaunchFailedCleaned, cmd.AgentId);
    }

    /// <summary>Ends a failed resume's agent unless its own exit teardown already has.</summary>
    async Task RetireAsync(AgentInstance agent) {
        try {
            if (_agents.TryGetValue(agent.Id, out var live) && ReferenceEquals(live, agent) && !agent.IsCleanupStarted) {
                await StopAgentCoreAsync(agent);
            } else if (!agent.Runtime.HasExited) {
                await agent.Runtime.TerminateAsync(ShutdownTerminateGrace);
            }
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            LogInPlaceRetireFailed(ex, agent.Id);
        }
    }

    ResumeLedgerEntry InPlaceEntry(LaunchAgentCommand cmd, ResumeInPlaceLaunch inPlace, string native, LaunchConsentOutcome consent) {
        var now    = _time.GetUtcNow();
        var launch = new ResumeLaunchDescriptor(
            Prompt:           cmd.Prompt,
            Model:            cmd.Model,
            Effort:           cmd.Effort,
            RepoPath:         inPlace.Directory,
            WorktreePath:     inPlace.Directory,
            Work:             WorkLocation.InPlace,
            GitRelativeCwd:   null,
            SandboxPolicy:    cmd.CodexPosture?.Sandbox,
            ApprovalPolicy:   cmd.CodexPosture?.Approval,
            PermissionPreset: cmd.AcpPermissionPreset,
            McpConfigPath:    null,
            Title:            StartTitleOf(cmd)?.Text,
            TitleDerived:     StartTitleOf(cmd)?.Derived ?? true,
            IsPrivate:        false,
            Origin:           "server",
            RequesterUserId:  cmd.RequesterUserId,
            RequesterDisplay: cmd.RequesterDisplay,
            ConsentSource:    consent.Source,
            ConsentAt:        now.ToString("O"),
            FlowRunId:        null,
            FlowRole:         null,
            WorktreeOwnsBranch: false,
            PermissionMode:     cmd.PermissionMode,
            AccountDirectory:   inPlace.ConfigDir,
            AccountId:          inPlace.AccountId,
            ApprovalsReviewer:  cmd.CodexPosture?.ApprovalsReviewer);

        return NewRunningEntry(cmd.AgentId, cmd.Vendor, cmd.ExpectedRuntimeTransport ?? PreStartTransport,
            SessionIds.Canonical(inPlace.SourceSessionId), native, launch, prior: null) with {
            State = ResumeState.Resuming, ResumingAt = now,
        };
    }

    /// <summary>Renews the source's lease until disposed, then ends it. A renewal the server refuses is logged and the
    /// resume goes on: the lease only fences a takeover, and the agent it would fence is already starting.</summary>
    internal IAsyncDisposable KeepSourceLease(string token, string sourceSessionId) => new SourceLeaseKeeper(this, token, sourceSessionId);

    sealed class SourceLeaseKeeper : IAsyncDisposable {
        readonly AgentOrchestrator       _owner;
        readonly string                  _token;
        readonly CancellationTokenSource _stop = new();
        readonly Task                    _loop;

        public SourceLeaseKeeper(AgentOrchestrator owner, string token, string source) {
            _owner = owner;
            _token = token;
            _loop  = RenewAsync(source);
        }

        async Task RenewAsync(string source) {
            try {
                while (true) {
                    await Task.Delay(_owner.SourceLeaseRenewal, _owner._time, _stop.Token);

                    if (!await _owner._server.RenewHandoffTakeoverAsync(_token, _stop.Token)) _owner.LogSourceLeaseLost(source);
                }
            } catch (OperationCanceledException) {
                // Disposed.
            }
        }

        public async ValueTask DisposeAsync() {
            await _stop.CancelAsync();
            await _loop;
            _stop.Dispose();
            await _owner._server.EndHandoffTakeoverAsync(_token);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "In-place resume: agent {AgentId} could not resume session {Session}: {Reason}")]
    partial void LogInPlaceResumeFailed(string agentId, string session, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "In-place resume: agent {AgentId} could not be retired")]
    partial void LogInPlaceRetireFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "In-place resume: the lease on session {Session} was not renewed")]
    partial void LogSourceLeaseLost(string session);
}
