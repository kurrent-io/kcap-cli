using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Handoff;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Captures LaunchFailedAsync calls and no-ops the other server-side methods
/// so the orchestrator's launch flow doesn't touch a real SignalR connection.
/// </summary>
sealed class CaptureServerConnection : ServerConnection {
    readonly DaemonConfig _config;

    public CaptureServerConnection(DaemonConfig? config = null)
        : this(config ?? new() { Name = "test", ServerUrl = "http://127.0.0.1:1" }, 0) { }

    CaptureServerConnection(DaemonConfig config, int _) : base(
            config, new FixedCredentialSource(), NullLoggerFactory.Instance, NullLogger<ServerConnection>.Instance, TimeProvider.System) =>
        _config = config;

    public            string?                               ConnectionIdForTest { get; init; } = "connection-1";
    internal override string?                               CurrentConnectionId => ConnectionIdForTest;
    readonly List<(string AgentId, string Reason)> _launchFailedCalls = [];

    /// <summary>A snapshot: the orchestrator reports a failed launch from its own tasks, so a test
    /// reading this while an agent is still winding down must not enumerate a list being appended.</summary>
    public IReadOnlyList<(string AgentId, string Reason)> LaunchFailedCalls {
        get { lock (_launchFailedCalls) return [.. _launchFailedCalls]; }
    }

    /// <summary>When set, LaunchFailedAsync still RECORDS the call (so a test can assert the
    /// send was attempted) but returns a faulted task — for the finalizer's verdict-report
    /// fault-containment test (a report fault must never skip CleanupAgentAsync/unregister).</summary>
    public Exception? LaunchFailedThrow { get; init; }

    /// <summary>Completed the instant LaunchFailedAsync is entered (after recording the call) —
    /// lets a test prove the finalizer is genuinely INSIDE its verdict-report await, so a
    /// concurrent status emitter (reconnect re-registration) can be driven in exactly that
    /// window (finding 2).</summary>
    public TaskCompletionSource? LaunchFailedEntered { get; init; }

    /// <summary>When set, LaunchFailedAsync awaits this task before returning — holding the
    /// finalizer's report open so a concurrent emission can race it deterministically.</summary>
    public TaskCompletionSource? LaunchFailedGate { get; init; }

    /// <summary>When set, EndAgentSessionAsync blocks until this token is cancelled,
    /// simulating a session-end call stuck waiting for a SignalR reconnect.</summary>
    public CancellationTokenSource? EndSessionBlockUntil { get; init; }

    /// <summary>Reasons passed to EndAgentSessionAsync, in call order.</summary>
    public List<string> EndSessionReasons { get; } = [];

    int _registerDaemonCalls;

    /// <summary>Full daemon re-registrations requested (the capability self-heal's
    /// <c>ReRegisterAsync</c> lands here). No-op'd: there is no hub to register with.</summary>
    public int RegisterDaemonCalls => Volatile.Read(ref _registerDaemonCalls);

    public Exception? RegisterDaemonThrow { get; set; }

    internal override Task RegisterDaemonAsync(RegistrationAttempt attempt) {
        Interlocked.Increment(ref _registerDaemonCalls);
        if (RegisterDaemonThrow is { } ex) return Task.FromException(ex);
        attempt.Sending(_config.Advertisement);
        attempt.Accept();
        return Task.CompletedTask;
    }

    /// <summary>The server's answer to each takeover begin; unset, the server lacks the method.</summary>
    public Func<string, string, HandoffTakeoverAdmission?>? BeginHandoff { get; set; }

    /// <summary>The server's answer to each lease renewal; unset, every renewal succeeds.</summary>
    public Func<string, CancellationToken, Task<bool>>? RenewHandoff { get; set; }

    public ConcurrentQueue<(string Source, string Successor, HandoffTakeoverAdmission? Admission)> HandoffBegins { get; } = new();
    public ConcurrentQueue<string> HandoffRenews { get; } = new();
    public ConcurrentQueue<string> HandoffEnds   { get; } = new();

    public override Task<HandoffTakeoverAdmission?> BeginHandoffTakeoverAsync(string source, string successor, CancellationToken ct) {
        var admission = BeginHandoff?.Invoke(source, successor);
        HandoffBegins.Enqueue((source, successor, admission));

        return Task.FromResult(admission);
    }

    public override Task<bool> RenewHandoffTakeoverAsync(string token, CancellationToken ct) {
        HandoffRenews.Enqueue(token);

        return RenewHandoff?.Invoke(token, ct) ?? Task.FromResult(true);
    }

    public override Task EndHandoffTakeoverAsync(string token) {
        HandoffEnds.Enqueue(token);

        return Task.CompletedTask;
    }

    public override async Task LaunchFailedAsync(string agentId, string reason) {
        lock (_launchFailedCalls) _launchFailedCalls.Add((agentId, reason));
        LaunchFailedEntered?.TrySetResult();

        if (LaunchFailedGate is { } gate) await gate.Task.ConfigureAwait(false);
        if (LaunchFailedThrow is { } ex) throw ex;
    }

    /// <summary>Every <see cref="DaemonStatusReport"/> the orchestrator sent, in order — the
    /// out-of-cycle launch-stage reports as well as any periodic tick. Lets a launch-path test
    /// inspect what the WIRE carried at each point, which is the only way to observe the
    /// handshake window (no AgentInstance exists during it).</summary>
    readonly Lock                     _statusReportGate = new();
    readonly List<DaemonStatusReport> _statusReports    = [];

    public IReadOnlyList<DaemonStatusReport> StatusReports {
        get { lock (_statusReportGate) return [.. _statusReports]; }
    }

    public int StatusReportCount { get { lock (_statusReportGate) return _statusReports.Count; } }

    public override Task DaemonStatusReportAsync(DaemonStatusReport report) {
        lock (_statusReportGate) _statusReports.Add(report);

        return Task.CompletedTask;
    }

    /// <summary>Number of times to fail AgentRegisteredAsync before succeeding (drives
    /// the bounded per-agent re-registration retry test).</summary>
    public int AgentRegisteredFailTimes { get; init; }
    public int AgentRegisteredCallCount { get; private set; }

    /// <summary>Completed the instant AgentRegisteredAsync is entered — lets a test hold
    /// re-registration inside that await and publish a verdict there (finding 1 refinement).</summary>
    public TaskCompletionSource? AgentRegisteredEntered { get; set; }

    /// <summary>When set, AgentRegisteredAsync awaits this before returning — holding the
    /// re-registration open across the AgentRegistered await.</summary>
    public TaskCompletionSource? AgentRegisteredGate { get; set; }

    /// <summary>When set, every non-failure AgentStatusChanged send checks this runtime's verdict
    /// AT SEND TIME (via the lock-synchronised ReadVerdict); if a launch-window verdict is already
    /// published, <see cref="NonFailureStatusSentAfterVerdictPublished"/> latches true — the exact
    /// "non-failure status after publication" invariant violation finding 1 closes.</summary>
    public ITerminationVerdictSource? VerdictCaptureRuntime                     { get; set; }
    public bool                       NonFailureStatusSentAfterVerdictPublished { get; private set; }

    /// <summary>Every (agentId, model) pair the orchestrator registered — proves the AgentInstance
    /// the server sees carries the model the process actually runs (the pinned explicit-reviewer
    /// LaunchModel, not the dispatched cmd.Model).</summary>
    public List<(string AgentId, string? Model)> AgentRegisteredCalls { get; } = [];

    /// <summary>The applied Codex posture echoed on each registration, in call order — proves the
    /// initial registration and every reconnect re-registration report the same pair.</summary>
    public List<(string AgentId, string? Sandbox, string? Approval)> AgentRegisteredPostures { get; } = [];

    /// <summary>The runtime transport ("pty" | "app-server") echoed on each registration, in call
    /// order — proves initial and reconnect re-registration report the same value.</summary>
    public List<(string AgentId, string? Transport)> AgentRegisteredTransports { get; } = [];
    public List<(string AgentId, string? Vendor)> AgentRegisteredVendors { get; } = [];

    /// <summary>The account each registration named, in call order.</summary>
    public List<(string AgentId, string? Directory, string? AccountId, string? Label)> AgentRegisteredAccounts { get; } = [];

    /// <summary>The start title echoed on each registration, in call order.</summary>
    public List<(string AgentId, AgentStartTitle? Title)> AgentRegisteredTitles { get; } = [];

    /// <summary>The agent each registration says it continues, in call order.</summary>
    public List<(string AgentId, string? ContinuesAgentId)> AgentRegisteredContinues { get; } = [];

    /// <summary>The server's answer per agent id; accepted when unset or null.</summary>
    public Func<string, AgentRegisteredResponse?>? RegistrationResponse { get; set; }

    public override async Task<AgentRegisteredResponse> AgentRegisteredAsync(
        string  agentId,              string? prompt, string? model, string? effort, string? repoPath,
        string? sandboxPolicy = null, string? approvalPolicy = null, string? permissionPreset = null,
        string? runtimeTransport = null, string? vendor = null, AgentStartTitle? title = null, string? continuesAgentId = null,
        string? accountDirectory = null, string? accountId = null, string? accountLabel = null) {
        AgentRegisteredCallCount++;
        lock (AgentRegisteredContinues) AgentRegisteredContinues.Add((agentId, continuesAgentId));
        lock (AgentRegisteredTitles) AgentRegisteredTitles.Add((agentId, title));
        lock (AcpCallOrder) AcpCallOrder.Add($"register:{agentId}");
        lock (AgentRegisteredCalls) AgentRegisteredCalls.Add((agentId, model));
        lock (AgentRegisteredPostures) AgentRegisteredPostures.Add((agentId, sandboxPolicy, approvalPolicy));
        lock (AgentRegisteredTransports) AgentRegisteredTransports.Add((agentId, runtimeTransport));
        lock (AgentRegisteredVendors) AgentRegisteredVendors.Add((agentId, vendor));
        lock (AgentRegisteredAccounts) AgentRegisteredAccounts.Add((agentId, accountDirectory, accountId, accountLabel));

        AgentRegisteredEntered?.TrySetResult();
        if (AgentRegisteredGate is { } gate) await gate.Task.ConfigureAwait(false);

        if (AgentRegisteredCallCount <= AgentRegisteredFailTimes)
            throw new InvalidOperationException("transient re-register failure");

        return RegistrationResponse?.Invoke(agentId) ?? AgentRegisteredResponse.Accepted;
    }

    // ── Option B task 4: ACP bind/forward capture ────────────────────────────────────
    // Overrides the RAW hub-invoke seams (mirroring AcpServerConnectionTests' TestServerConnection)
    // rather than the higher-level gated AcpSessionStartedAsync/SendAcpEventsAsync, so every call
    // still goes through the REAL ConnectionRetry/IsReady gating the production wiring relies on.
    // IsReady is overridden to true (no real hub connection exists in these tests) so that gating
    // resolves immediately instead of hanging forever waiting for a connection that never connects.

    /// <summary>Settable so a test can hold the daemon inside its registration bracket.</summary>
    public bool Ready { get; set; } = true;

    internal override bool IsReady => Ready;

    /// <summary>Every register/bind/events call, in the exact order the orchestrator issued them —
    /// the single source of truth for the bind-ordering and teardown-ordering assertions.</summary>
    public List<string> AcpCallOrder { get; } = [];

    public List<(string AgentId, string Vendor, string AcpSessionId, string? Cwd, string? Model)> AcpSessionStartedCalls { get; } = [];
    public List<(string AgentId, string AcpSessionId, AcpEventEnvelope[] Envelopes)>              AcpEventsCalls         { get; } = [];

    /// <summary>Fires (unbounded, never blocks the caller) once per AcpSessionEvents call, carrying
    /// the 1-based call count — lets a test await "the Nth events call happened" deterministically
    /// instead of guessing with Task.Delay.</summary>
    public Channel<int> AcpEventsCallSignal { get; } = Channel.CreateUnbounded<int>();

    /// <summary>Overrides the ack a given batch receives; defaults to "fully accepted".</summary>
    public Func<AcpEventEnvelope[], AcpBatchAck>? AcpEventsAckOverride { get; init; }

    /// <summary>Set to make the raw AcpSessionEvents invoke hang until this token is cancelled —
    /// simulates a server call that never returns, for the bounded-final-drain test (mirrors
    /// EndSessionBlockUntil's pattern).</summary>
    public CancellationTokenSource? AcpEventsBlockUntil { get; init; }

    /// <summary>One-shot gate: when set, the NEXT raw AcpSessionEvents invoke awaits this task
    /// before returning (then the field is cleared) — lets a test deterministically control
    /// exactly when one specific events call completes, without racing unrelated background
    /// work (e.g. CleanupAgentAsync's own worktree removal) that would otherwise let a call
    /// "eventually" go through regardless of the ordering under test.</summary>
    public TaskCompletionSource? PendingAcpEventsGate { get; set; }

    /// <summary>One-shot gate: when set, the NEXT raw AcpSessionStarted invoke awaits this task
    /// before returning (then the field is cleared) — models a bind call still in flight across
    /// a reconnect outage (reliability fix's stale-binding-race test), independent of
    /// <c>ct</c> so the test controls exactly when the "late bind" resolves.</summary>
    public TaskCompletionSource? PendingAcpBindGate { get; set; }

    internal override async Task<AcpBindOutcome> InvokeAcpSessionStartedRawAsync(
            string                               agentId,  string            vendor, string acpSessionId, string? cwd, string? model,
            IReadOnlyDictionary<string, string>? metadata, CancellationToken ct
        ) {
        lock (AcpCallOrder) {
            AcpCallOrder.Add($"bind:{agentId}");
            AcpSessionStartedCalls.Add((agentId, vendor, acpSessionId, cwd, model));
        }

        if (PendingAcpBindGate is { } gate) {
            PendingAcpBindGate = null;
            await gate.Task;
        }

        return AcpBindOutcome.Bound;
    }

    internal override async Task<AcpBatchAck> InvokeAcpSessionEventsRawAsync(
            string agentId, string acpSessionId, AcpEventEnvelope[] envelopes, CancellationToken ct
        ) {
        int callCount;

        lock (AcpCallOrder) {
            AcpCallOrder.Add($"events:{agentId}:{string.Join(',', envelopes.Select(e => e.Seq))}");
            AcpEventsCalls.Add((agentId, acpSessionId, envelopes));
            callCount = AcpEventsCalls.Count;
        }

        if (AcpEventsBlockUntil is { } blockCts) {
            // Linked with ct (unlike a bare blockCts.Token wait) so a per-agent CTS cancellation
            // propagates through exactly like a real _hub.InvokeAsync(..., cancellationToken: ct)
            // call would (reliability fix: forwarder-cancel-on-drain-timeout relies on
            // this). A ct-driven cancellation PROPAGATES (mirrors the real hub); blockCts alone
            // is purely test cleanup (releases an otherwise-abandoned background call) and falls
            // through to a normal successful return.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(blockCts.Token, ct);

            try {
                await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            } catch (OperationCanceledException) {
                /* released by the test (blockCts) */
            }
        }

        // Not interlocked: the forwarder always has exactly one send in flight at a time (its
        // single-in-flight-batch design), so this test double never sees concurrent callers.
        if (PendingAcpEventsGate is { } gate) {
            PendingAcpEventsGate = null;
            await gate.Task;
        }

        AcpEventsCallSignal.Writer.TryWrite(callCount);

        return AcpEventsAckOverride?.Invoke(envelopes)
            ?? (envelopes.Length == 0 ? new AcpBatchAck(-1, -1) : new AcpBatchAck(envelopes[^1].Seq, envelopes[^1].Seq));
    }

    // ── §2.5 source-claim / confirm seams ───────────────────────────────────────────────

    /// <summary>Outcome the raw AcpSessionSourceClaim invoke returns; default Bound@token-1. A Rejected
    /// outcome models the server declining ownership.</summary>
    public AcpSourceClaimOutcome? SourceClaimOutcome { get; init; }

    /// <summary>When set, the raw AcpSessionSourceClaim invoke returns a FAULTED task (after recording) —
    /// models a pre-source-claim server whose hub method doesn't exist (method-not-found).</summary>
    public Exception? SourceClaimThrow { get; init; }

    /// <summary>Fires (unbounded) once per source-claim call.</summary>
    public Channel<int> SourceClaimSignal { get; } = Channel.CreateUnbounded<int>();

    /// <summary>Outcome the raw ConfirmSessionLaunch invoke returns; default Confirmed. A custom function
    /// sees the 1-based call count, so a test can model transient-then-terminal.</summary>
    public Func<int, AcpLaunchConfirmOutcome>? ConfirmOutcome { get; init; }

    /// <summary>Ownership tokens passed to each confirm, in order; its count fires ConfirmSignal.</summary>
    public List<long>   ConfirmTokens { get; } = [];
    public Channel<int> ConfirmSignal { get; } = Channel.CreateUnbounded<int>();

    internal override Task<AcpSourceClaimOutcome> InvokeAcpSessionSourceClaimRawAsync(
            string agentId, string acpSessionId, CancellationToken ct) {
        lock (AcpCallOrder) AcpCallOrder.Add($"sourceClaim:{agentId}");
        SourceClaimSignal.Writer.TryWrite(1);

        if (SourceClaimThrow is { } ex)
            return Task.FromException<AcpSourceClaimOutcome>(ex); // method-not-found on a pre-source-claim server

        return Task.FromResult(SourceClaimOutcome ?? new AcpSourceClaimOutcome(AcpBindOutcome.Bound, 1, -1));
    }

    internal override Task<AcpLaunchConfirmOutcome> InvokeConfirmSessionLaunchRawAsync(
            string acpSessionId, long ownershipToken, CancellationToken ct) {
        int count;
        lock (AcpCallOrder) {
            AcpCallOrder.Add($"confirm:{ownershipToken}");
            ConfirmTokens.Add(ownershipToken);
            count = ConfirmTokens.Count;
        }
        ConfirmSignal.Writer.TryWrite(count);

        return Task.FromResult(ConfirmOutcome?.Invoke(count) ?? AcpLaunchConfirmOutcome.Confirmed);
    }

    /// <summary>(AgentId, Status) pairs passed to every AgentStatusChangedAsync call, in
    /// call order — lets a test assert on the exact lifecycle transitions the orchestrator
    /// drove (e.g. Fix B/E's immediate Running flip for a no-terminal runtime).</summary>
    public List<(string AgentId, string Status)> StatusChangedCalls { get; } = [];

    /// <summary>The same calls with the session id the daemon reported alongside each status — the
    /// only place a test can see whether the FIRST status a server saw already named the session.</summary>
    public List<(string AgentId, string Status, string? SessionId)> StatusChangedWithSession { get; } = [];

    /// <summary>Thrown by every AgentStatusChangedAsync call while set.</summary>
    public Exception? StatusChangedThrow { get; set; }

    public override Task AgentStatusChangedAsync(string agentId, string status, string? sessionId) {
        if (StatusChangedThrow is { } ex) return Task.FromException(ex);

        // Capture BEFORE recording: was a launch-window verdict already published when this
        // non-failure status was sent? That ordering is the invariant a check-to-send race breaks.
        if (status is "Completed" or "Running" or "Starting"
         && VerdictCaptureRuntime?.ReadVerdict() is { ReapedInsideLaunchWindow: true })
            NonFailureStatusSentAfterVerdictPublished = true;

        lock (StatusChangedCalls) StatusChangedCalls.Add((agentId, status));
        lock (StatusChangedWithSession) StatusChangedWithSession.Add((agentId, status, sessionId));

        return Task.CompletedTask;
    }

    /// <summary>Invoked when AgentUnregisteredAsync runs — the last step of
    /// CleanupAgentAsync, so a useful signal that local cleanup completed.</summary>
    public Action? OnAgentUnregistered { get; init; }

    /// <summary>Every agent id passed to AgentUnregisteredAsync, in call order. Phase B
    /// (D1): a single-flight teardown must unregister an agent exactly once even under a racing
    /// launch-catch + read-loop cleanup.</summary>
    public List<string> AgentUnregisteredCalls { get; } = [];

    public Dictionary<string, string?> AgentUnregisteredStopReasons { get; } = [];

    /// <summary>Every dropped-input report, in call order — the only place a drop reason becomes
    /// observable to anyone but this daemon's own log.</summary>
    public List<(Guid DispatchId, string AgentId, string Reason)> InputRejections { get; } = [];

    public override Task SendInputRejectedAsync(Guid dispatchId, string agentId, string reason) {
        lock (InputRejections) InputRejections.Add((dispatchId, agentId, reason));

        return Task.CompletedTask;
    }

    public override Task AgentUnregisteredAsync(string agentId, string? stopReason = null) {
        lock (AgentUnregisteredCalls) {
            AgentUnregisteredCalls.Add(agentId);
            AgentUnregisteredStopReasons[agentId] = stopReason;
        }
        OnAgentUnregistered?.Invoke();

        return Task.CompletedTask;
    }

    public override Task UpdateRepoPathsAsync()
        => Task.CompletedTask;

    /// <summary>Set both to make every terminal send block until its <c>ct</c> cancels, then throw —
    /// so a send configured this way never reaches <see cref="TerminalSendGate"/> or
    /// <see cref="TerminalSends"/>, and a test that sets both alongside this is exercising only this.</summary>
    public TaskCompletionSource? SendEntered   { get; init; }
    public TaskCompletionSource? SendUnblocked { get; init; }

    /// <summary>Runs before a terminal send is recorded; a test gates or fails the send here.</summary>
    public Func<string, CancellationToken, Task>? TerminalSendGate { get; set; }

    public ConcurrentQueue<(string AgentId, byte[] Data)> TerminalSends { get; } = new();

    int _terminalSendStarts;
    public int TerminalSendStarts => Volatile.Read(ref _terminalSendStarts);

    public override async Task SendTerminalOutputAsync(string agentId, string base64Data, CancellationToken ct = default) {
        Interlocked.Increment(ref _terminalSendStarts);

        if (SendEntered is not null) {
            SendEntered.TrySetResult();

            try {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            } finally {
                SendUnblocked?.TrySetResult();
            }
        }

        if (TerminalSendGate is { } gate) await gate(agentId, ct);

        TerminalSends.Enqueue((agentId, Convert.FromBase64String(base64Data)));
    }

    /// <summary>Every (agentId, event) pair passed to AppendAgentRunEventAsync, in call order.</summary>
    public List<(string AgentId, object Event)> RunEvents { get; } = [];

    /// <summary>When set, AppendAgentRunEventAsync records the event, then fails with this.</summary>
    public Exception? RunEventThrow { get; set; }

    public override Task AppendAgentRunEventAsync(string agentId, object evt) {
        lock (RunEvents) RunEvents.Add((agentId, evt));

        return RunEventThrow is { } ex ? Task.FromException(ex) : Task.CompletedTask;
    }

    /// <summary>Every shutdown report, with the state of the token it was handed. The token matters
    /// more than the arguments: every other method on the real connection bakes in one that is
    /// already cancelled during teardown, so a report made with a cancelled token sends nothing and
    /// says nothing about it — a double that ignored the token would pass either way.</summary>
    public List<(string AgentId, string Status, string Reason, bool TokenAlreadyCancelled)> ShutdownReports { get; } = [];

    /// <summary>When set, the shutdown report blocks on this until its own token cancels — the hung
    /// server a shutdown must not wait on.</summary>
    public bool ShutdownReportHangs { get; init; }

    public List<(string AgentId, int? ExitCode)> ShutdownReportExitCodes { get; } = [];

    public override async Task ReportAgentEndedForShutdownAsync(
            string agentId, string? sessionId, string status, string reason, int? exitCode, CancellationToken ct) {
        lock (ShutdownReports) ShutdownReports.Add((agentId, status, reason, ct.IsCancellationRequested));
        lock (ShutdownReportExitCodes) ShutdownReportExitCodes.Add((agentId, exitCode));

        if (ShutdownReportHangs) await Task.Delay(Timeout.Infinite, ct);
        if (ShutdownReportGate is { } gate) await gate.Task.WaitAsync(ct);
    }

    /// <summary>When set, the shutdown report is recorded and then held until the gate completes.</summary>
    public TaskCompletionSource? ShutdownReportGate { get; set; }

    // ── Task 8: resolved-model report capture ──────────────────────────────────
    /// <summary>Every legacy (agentId, model) pair passed to ReportAgentResolvedModelAsync — proves
    /// the no-model / vendor-only launch keeps using the unchanged legacy channel.</summary>
    public List<(string AgentId, string Model)> ReportAgentResolvedModelCalls { get; } = [];

    /// <summary>Every explicit-model report passed to ReportExplicitReviewerModelResolvedAsync — proves
    /// an explicit-model launch reports the concrete resolved model on the dedicated v3 channel.</summary>
    public List<ExplicitReviewerModelResolvedV1> ExplicitReviewerModelReports { get; } = [];

    /// <summary>Signals each ExplicitReviewerModelResolvedAsync call (1-based count) so a test can
    /// await the fire-and-forget report deterministically instead of racing Task.Delay.</summary>
    public Channel<int> ExplicitReviewerModelReportSignal { get; } = Channel.CreateUnbounded<int>();

    public List<string> CommandReports { get; } = [];

    public override Task ReportAgentCommandsAsync(string agentId, IReadOnlyList<HostedAgentCommand> commands) {
        lock (CommandReports) CommandReports.Add(agentId);
        return Task.CompletedTask;
    }

    public List<(string AgentId, TerminalDialogDto? Dialog)> TerminalDialogReports { get; } = [];

    /// <summary>Runs as each dialog report is sent, before the next one can be.</summary>
    public Action<string, TerminalDialogDto?>? OnTerminalDialogReport { get; set; }

    /// <summary>When set, each dialog report stays in flight until this completes.</summary>
    public TaskCompletionSource? TerminalDialogReportHold { get; set; }

    public override Task ReportAgentTerminalDialogAsync(string agentId, TerminalDialogDto? dialog) {
        lock (TerminalDialogReports) TerminalDialogReports.Add((agentId, dialog));
        OnTerminalDialogReport?.Invoke(agentId, dialog);
        return TerminalDialogReportHold?.Task ?? Task.CompletedTask;
    }

    public override Task ReportAgentResolvedModelAsync(string agentId, string model) {
        lock (ReportAgentResolvedModelCalls) ReportAgentResolvedModelCalls.Add((agentId, model));

        return Task.CompletedTask;
    }

    public override Task ReportExplicitReviewerModelResolvedAsync(ExplicitReviewerModelResolvedV1 report) {
        int count;
        lock (ExplicitReviewerModelReports) {
            ExplicitReviewerModelReports.Add(report);
            count = ExplicitReviewerModelReports.Count;
        }
        ExplicitReviewerModelReportSignal.Writer.TryWrite(count);

        return Task.CompletedTask;
    }

    // ── §2.7 B6 arm-A: participant-park report capture ───────────────────────────────
    /// <summary>The ack <see cref="ReportParticipantParkedAsync"/> returns; default
    /// <see cref="ParkAck.Parked"/>. A park state-machine test sets this to drive the three branches
    /// (Parked / Rejected / Ambiguous) deterministically.</summary>
    public ParkAck ParkOutcome { get; set; } = ParkAck.Parked;

    /// <summary>Every (agentId, canonicalSessionId, reason) passed to
    /// <see cref="ReportParticipantParkedAsync"/>, in call order — lets a test assert the canonical
    /// thread id and reason the daemon reported, and (by emptiness) that a park that aborted at the
    /// claim never told the server at all.</summary>
    public List<(string AgentId, string CanonicalSessionId, string Reason)> ParkReports { get; } = [];

    /// <summary>Signalled the moment <see cref="ReportParticipantParkedAsync"/> is entered (the park
    /// report is in flight) so a test can interleave a racing child-exit/finalize in the ack-await
    /// window. Null = no signal.</summary>
    public TaskCompletionSource? ParkEntered { get; init; }

    /// <summary>Held-open gate: when set, <see cref="ReportParticipantParkedAsync"/> awaits it before
    /// returning <see cref="ParkOutcome"/>, keeping the park ack in flight while a test exercises a
    /// concurrent finalize. Null = return the ack immediately.</summary>
    public TaskCompletionSource? ParkGate { get; init; }

    public override async Task<ParkAck> ReportParticipantParkedAsync(
            string agentId, string canonicalSessionId, string reason, CancellationToken ct = default) {
        lock (ParkReports) ParkReports.Add((agentId, canonicalSessionId, reason));
        ParkEntered?.TrySetResult();
        if (ParkGate is { } gate) await gate.Task;

        return ParkOutcome;
    }

    public override async Task<EndAgentSessionResult> EndAgentSessionAsync(string agentId, string reason) {
        EndSessionReasons.Add(reason);
        lock (AcpCallOrder) AcpCallOrder.Add($"endSession:{agentId}");

        if (EndSessionBlockUntil is { } cts) {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token); } catch (OperationCanceledException) {
                /* released by the test */
            }
        }

        return new EndAgentSessionResult();
    }

    public override Task<PermissionDecision> RequestPermissionAsync(
            string            sessionId,
            string?           toolName,
            JsonElement?      toolInput,
            JsonElement?      suggestions,
            CancellationToken ct = default
        ) => Task.FromResult(new PermissionDecision("deny", null, null));
}
