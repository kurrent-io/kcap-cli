using System.Text.Json;
using System.Text.RegularExpressions;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Continuation;
using Capacitor.Cli.Core.Handoff;
using Capacitor.Cli.Core.LocalIpc;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

internal partial class AgentOrchestrator {
    internal TimeSpan HandoffProbeDeadline { get; set; } = TimeSpan.FromSeconds(50);

    internal Func<string, HandoffTarget, CancellationToken, Task<HandoffResolution>>? ResolveHandoffForTest { get; set; }

    /// <summary>Resolves a hand-off's starting code for the app. Every outcome is one reply; a payload
    /// that does not parse gets an <c>invalid_target</c> refusal rather than an Error frame.</summary>
    public async Task HandleLocalHandoffProbeAsync(string payload, Stream stream, CancellationToken ct) {
        HandoffProbeReplyDto reply;
        try {
            var request = JsonSerializer.Deserialize(payload, HandoffIpcJsonContext.Default.HandoffProbeRequestDto);
            reply = request is null ? Refused(HandoffRefusals.InvalidTarget) : await ProbeHandoffAsync(request, ct);
        } catch (JsonException) {
            reply = Refused(HandoffRefusals.InvalidTarget);
        }

        try {
            var json = JsonSerializer.Serialize(reply, HandoffIpcJsonContext.Default.HandoffProbeReplyDto);
            await FrameCodec.WriteAsync(stream, LocalFrame.HandoffJson(FrameType.HandoffProbeReply, json), ct);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            _logger.LogDebug(ex, "HandoffProbe: the reply could not be written");
        }
    }

    /// <summary>The allow-list and the origin identity are decided before anything is fetched, so a path
    /// or a checkout the daemon has no business reading never reaches the network. A probe that outlives
    /// <see cref="HandoffProbeDeadline"/> is answered as a fetch error; its git processes may keep running.</summary>
    internal async Task<HandoffProbeReplyDto> ProbeHandoffAsync(HandoffProbeRequestDto request, CancellationToken ct) {
        var authorized = await new BorrowAuthorizer(_config).AuthorizeBorrowAsync(request.RepoPath);
        if (!authorized.Allowed) return Refused(HandoffRefusals.RepoNotAllowed);

        if (authorized.CanonicalGitRoot is not { } root || !OriginIs(root, request.RepoOwner, request.RepoName))
            return Refused(HandoffRefusals.RepoMismatch);

        using var expiry = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var work     = ReadHandoffAsync(root, request, ct);
        var deadline = Task.Delay(HandoffProbeDeadline, _time, expiry.Token);

        if (await Task.WhenAny(work, deadline) != work) {
            ct.ThrowIfCancellationRequested();

            return FetchFailed("timed out waiting for origin");
        }

        await expiry.CancelAsync();

        return await work;
    }

    async Task<HandoffProbeReplyDto> ReadHandoffAsync(string root, HandoffProbeRequestDto request, CancellationToken ct) {
        try {
            var target     = new HandoffTarget(request.Branch, request.PullRequestNumber, request.HeadIsFork);
            var resolution = await (ResolveHandoffForTest?.Invoke(root, target, ct)
                ?? _worktreeManager.ResolveHandoffStartAsync(root, target, ct));
            if (resolution.Start is not { } start)
                return FetchFailed(resolution.FetchError ?? "origin could not be read");

            var work = await _worktreeManager.CheckHandoffWorkAsync(root, start.Sha, request.SessionCommits, mayHaveUnpushedWork: true);
            var launch = new HandoffLaunchStart(
                "", start.Sha, start.Checkout.ToString(), start.Branch, start.LocalBranchSha, start.Source.ToString(),
                start.DetachReason.ToString(), request.PullRequestNumber, request.HeadIsFork, request.HeadOwner, request.HeadRepo);

            return new(null, null, launch, work.Completeness.ToString(), work.MissingCommits);
        } catch (Exception ex) when (WorktreeManager.IsHandoffTargetRefusal(ex)) {
            _logger.LogDebug(ex, "HandoffProbe: the target was refused");

            return Refused(HandoffRefusals.InvalidTarget);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            _logger.LogWarning(ex, "HandoffProbe: reading the checkout failed");

            return FetchFailed(ex.Message);
        }
    }

    static HandoffProbeReplyDto FetchFailed(string detail) => new(null, UserInfo().Replace(detail, "://"), null, null, null);

    [GeneratedRegex(@"://[^/@\s]*@")]
    private static partial Regex UserInfo();

    static bool OriginIs(string gitRoot, string owner, string name) {
        var (originOwner, originName) = GitUrlParser.ParseRemoteUrl(
            GitRemoteReader.ReadOriginUrl(GitRepository.ResolveMainRepoRoot(gitRoot)));

        return string.Equals(originOwner, owner, StringComparison.OrdinalIgnoreCase)
            && string.Equals(originName, name, StringComparison.OrdinalIgnoreCase);
    }

    static HandoffProbeReplyDto Refused(string refusal) => new(refusal, null, null, null, null);

    /// <summary>Why a launch cannot carry its hand-off block, or null when it can or carries none. Only an
    /// interactive launch into a fresh owned worktree can: any other shape would start somewhere the probe
    /// never looked, so the block is refused rather than ignored.</summary>
    internal static string? HandoffLaunchRefusal(LaunchAgentCommand cmd, bool resuming) =>
        cmd.Handoff switch {
            null => null,
            _ when resuming || cmd.ResumeSessionId is not null => "invalid_handoff: a resumed launch cannot hand off",
            _ when cmd.Kind != LaunchKind.Default              => "invalid_handoff: a review launch cannot hand off",
            _ when cmd.Borrowed                                => "invalid_handoff: a borrowed launch cannot hand off",
            { Start: null } or { Source: null }                => "invalid_handoff: the block is incomplete",
            { Start: var start } when start.ToStart() is null  => "invalid_handoff: the start's shape is not one this daemon knows",
            _                                                  => null
        };

    /// <summary>A derived title naming the session handed off, by its title or else by the start of its id.</summary>
    internal static AgentStartTitle HandoffStartTitle(HandoffSourceContext source) {
        var id    = source.SessionId ?? "";
        var named = AgentStartTitle.Shorten(source.Title) ?? id[..Math.Min(8, id.Length)];

        return AgentStartTitle.FromPrompt($"Hand-off of {named}")!.Value;
    }

    /// <summary>Creates the hand-off worktree at the launch's start and builds the prompt that replaces the
    /// user's text. A start that moved since the probe throws <c>handoff_start_changed</c> and leaves no
    /// worktree behind.</summary>
    internal async Task<(WorktreeInfo? Worktree, string? Prompt, string? Refusal)> PrepareHandoffAsync(
            LaunchAgentCommand cmd, string repoPath, CancellationToken ct) {
        if (cmd.Handoff is not { } handoff) return (null, null, "invalid_handoff: the launch carries no hand-off");
        if (HandoffLaunchRefusal(cmd, resuming: false) is { } refusal) return (null, null, refusal);

        var (owner, name) = (handoff.Source.RepoOwner ?? "", handoff.Source.RepoName ?? "");
        if (!OriginIs(repoPath, owner, name))
            return (null, null, $"{HandoffRefusals.RepoMismatch}: {repoPath} is not a checkout of {owner}/{name}");

        var start = handoff.Start.ToStart()!;
        ct.ThrowIfCancellationRequested();

        var work     = await _worktreeManager.CheckHandoffWorkAsync(repoPath, start.Sha, handoff.SessionCommits, mayHaveUnpushedWork: true);
        var worktree = await _worktreeManager.CreateHandoffAsync(repoPath, start);

        return (worktree, HandoffPrompt.Build(handoff.Source, handoff.Start, start, work, cmd.Prompt), null);
    }

    internal const string SourceReservationHeader = "X-Kcap-Source-Reservation";

    const string SuccessorUnknown = "successor_unknown";

    internal TimeSpan HandoffLeaseRenewal    { get; set; } = TimeSpan.FromSeconds(20);
    internal TimeSpan HandoffRenewTimeout    { get; set; } = TimeSpan.FromSeconds(10);
    internal TimeSpan HandoffSuccessorRetry  { get; set; } = TimeSpan.FromSeconds(2);
    internal TimeSpan HandoffSuccessorWindow { get; set; } = TimeSpan.FromSeconds(60);
    internal TimeSpan HandoffShutdownGrace   { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Replaces the takeover run, given the run's client, the source, the successor and the run's token.</summary>
    internal Func<HttpClient, string, string, CancellationToken, Task<TakeoverResult>>? RunHandoffTakeoverForTest { get; set; }

    /// <summary>Answers with the outcome kept for a successor session, or none. The store outlives the agent, so a
    /// session that has ended is answered too.</summary>
    public async Task HandleLocalHandoffOutcomeQueryAsync(string sessionId, Stream stream, CancellationToken ct) {
        var reply = new HandoffOutcomeReplyDto(_handoffOutcomes.Get(sessionId));

        try {
            var json = JsonSerializer.Serialize(reply, HandoffIpcJsonContext.Default.HandoffOutcomeReplyDto);
            await FrameCodec.WriteAsync(stream, LocalFrame.HandoffJson(FrameType.HandoffOutcomeReply, json), ct);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            _logger.LogDebug(ex, "HandoffOutcome: the reply could not be written");
        }
    }

    /// <summary>Starts the takeover of a hand-off's source off the caller's path, once, as soon as the agent's own
    /// session exists; a terminal agent's pinned id names no session until its transcript is found. The agent runs
    /// on whatever the outcome.</summary>
    void StartHandoffTakeover(AgentInstance agent) {
        if (agent.HandoffSource is not { } source || SessionIds.Canonical(agent.SessionId) is not { } successor) return;
        if (agent.Runtime is not IAcpTranscriptSource && agent.TranscriptPath is null) return;
        if (Interlocked.CompareExchange(ref agent.HandoffTakeoverStarted, 1, 0) != 0) return;

        PutHandoffOutcome(successor, HandoffOutcomeDto.Running(source.SessionId, source.Title));
        TrackHandoffTakeover(HandOffAsync(agent.Id, source, successor, _shutdownCts.Token));
    }

    readonly Lock          _handoffTakeoversLock = new();
    readonly HashSet<Task> _handoffTakeovers     = [];

    void TrackHandoffTakeover(Task run) {
        lock (_handoffTakeoversLock) _handoffTakeovers.Add(run);
        _ = run.ContinueWith(done => { lock (_handoffTakeoversLock) _handoffTakeovers.Remove(done); }, TaskScheduler.Default);
    }

    /// <summary>Runs after the shutdown token is cancelled and before the hub goes, so a takeover that already
    /// finished can store its outcome and every run can end its lease.</summary>
    async Task DrainHandoffTakeoversAsync() {
        Task[] runs;
        lock (_handoffTakeoversLock) runs = [.. _handoffTakeovers];
        if (runs.Length == 0) return;

        try {
            await Task.WhenAll(runs).WaitAsync(HandoffShutdownGrace, _time);
        } catch (TimeoutException) {
            LogHandoffTakeoversAbandoned(runs.Count(r => !r.IsCompleted));
        }
    }

    /// <summary>Never throws. A run that shutdown cut short stores nothing, so the next start reads the
    /// <c>running</c> it leaves as interrupted.</summary>
    async Task HandOffAsync(string agentId, HandoffSourceContext source, string successor, CancellationToken ct) {
        HandoffOutcomeDto outcome;
        try {
            outcome = await TakeOverHandoffAsync(agentId, source, successor, ct);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            return;
        } catch (Exception ex) {
            LogHandoffTakeoverFailed(ex, agentId, source.SessionId);
            outcome = new HandoffOutcomeDto(source.SessionId, source.Title, "failed", ex.Message, []);
        }

        PutHandoffOutcome(successor, outcome);
        LogHandoffTakeover(agentId, source.SessionId, successor, outcome.State, outcome.Detail);
    }

    /// <summary>Every request of the run carries the lease token, and the lease is renewed while it runs: a renewal
    /// that fails cancels the run at once, since the server may already have given the source to someone else.</summary>
    async Task<HandoffOutcomeDto> TakeOverHandoffAsync(string agentId, HandoffSourceContext source, string successor, CancellationToken ct) {
        var sourceWire = SessionIds.Canonical(source.SessionId) ?? source.SessionId;
        HandoffTakeoverAdmission admission;
        switch (await BeginHandoffTakeoverAsync(sourceWire, successor, ct)) {
            case HandoffBeginResult.Answered answered: admission = answered.Admission; break;
            case HandoffBeginResult.Unsupported:       return Failed(source, "server_unsupported");
            default:                                   return Failed(source, "server_unreachable");
        }
        if (admission.Token is not { } token) return HandoffOutcomeDto.Refused(source.SessionId, source.Title, admission.Refusal ?? "refused");

        using var run      = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var renewing = new CancellationTokenSource();
        var renewals = RenewHandoffLeaseAsync(agentId, token, run, renewing.Token);

        try {
            using var client = await _http.ForBackgroundAsync(run.Token);
            client.DefaultRequestHeaders.Add(SourceReservationHeader, token);

            var result = await (RunHandoffTakeoverForTest?.Invoke(client, sourceWire, successor, run.Token)
                ?? new SessionTakeover(new HostedSessionLiveness(HostsSession), _time)
                    .RunAsync(client, ServerBase, sourceWire, successor, TakeoverAdmission.EndedOnly, run.Token));
            if (result is TakeoverResult.Interrupted && ct.IsCancellationRequested) throw new OperationCanceledException(ct);

            return HandoffOutcomeDto.From(source.SessionId, source.Title, result);
        } finally {
            await renewing.CancelAsync();
            await renewals;
            await EndHandoffLeaseAsync(token);
        }
    }

    static HandoffOutcomeDto Failed(HandoffSourceContext source, string code) => new(source.SessionId, source.Title, "failed", code, []);

    /// <summary>The server can learn the successor's session after this daemon does, from the status that names
    /// it, so a begin it answers <c>successor_unknown</c> is retried for <see cref="HandoffSuccessorWindow"/>, and so
    /// is one that did not reach it.</summary>
    async Task<HandoffBeginResult> BeginHandoffTakeoverAsync(string source, string successor, CancellationToken ct) {
        var deadline = _time.GetUtcNow() + HandoffSuccessorWindow;

        while (true) {
            var begin = await _server.BeginHandoffTakeoverAsync(source, successor, ct);
            var retry = begin is HandoffBeginResult.Unreachable or HandoffBeginResult.Answered { Admission.Refusal: SuccessorUnknown };
            if (!retry || _time.GetUtcNow() + HandoffSuccessorRetry > deadline) return begin;

            await Task.Delay(HandoffSuccessorRetry, _time, ct);
        }
    }

    /// <summary>Never throws. False, an exception or no answer within <see cref="HandoffRenewTimeout"/> is a lost lease.</summary>
    async Task RenewHandoffLeaseAsync(string agentId, string token, CancellationTokenSource run, CancellationToken stop) {
        try {
            while (true) {
                await Task.Delay(HandoffLeaseRenewal, _time, stop);
                if (await RenewHandoffLeaseOnceAsync(token, stop)) continue;
                if (stop.IsCancellationRequested) return;

                LogHandoffLeaseLost(agentId);
                await run.CancelAsync();

                return;
            }
        } catch (OperationCanceledException) when (stop.IsCancellationRequested) {
        } catch (Exception ex) {
            LogHandoffTakeoverFailed(ex, agentId, "lease renewal");
            try { await run.CancelAsync(); } catch (ObjectDisposedException) { }
        }
    }

    async Task<bool> RenewHandoffLeaseOnceAsync(string token, CancellationToken stop) {
        using var expiry = new CancellationTokenSource(HandoffRenewTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, expiry.Token);

        try {
            return await _server.RenewHandoffTakeoverAsync(token, linked.Token).WaitAsync(linked.Token);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "HandoffTakeover: the lease renewal failed");

            return false;
        }
    }

    async Task EndHandoffLeaseAsync(string token) {
        try {
            await _server.EndHandoffTakeoverAsync(token);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "HandoffTakeover: ending the lease failed");
        }
    }

    void PutHandoffOutcome(string successor, HandoffOutcomeDto outcome) {
        try {
            _handoffOutcomes.Put(successor, outcome);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            LogHandoffOutcomeNotSaved(ex, successor);
        }
    }

    bool HostsSession(SessionId session) =>
        _agents.Values.Any(a => a.Status is not ("Completed" or "Failed") && SessionId.Parse(SessionIds.Canonical(a.SessionId)) == session);

    [LoggerMessage(Level = LogLevel.Information, Message = "Hand-off: agent {AgentId} session {Successor} took over {Source}: {State} {Detail}")]
    partial void LogHandoffTakeover(string agentId, string source, string successor, string state, string? detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hand-off: agent {AgentId} could not take over {Source}")]
    partial void LogHandoffTakeoverFailed(Exception ex, string agentId, string source);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hand-off: agent {AgentId} lost its takeover lease; the takeover stops")]
    partial void LogHandoffLeaseLost(string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hand-off: shutdown stopped waiting for {Count} takeover(s)")]
    partial void LogHandoffTakeoversAbandoned(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hand-off: the outcome for session {Successor} could not be saved")]
    partial void LogHandoffOutcomeNotSaved(Exception ex, string successor);
}
