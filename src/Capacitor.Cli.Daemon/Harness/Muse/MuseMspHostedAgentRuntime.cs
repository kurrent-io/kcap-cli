using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Harness.Muse;

/// <summary>
/// <see cref="IHostedAgentRuntime"/> for Muse Code, driving one long-lived <c>muse serve</c> child over
/// MSP. A control plane only: the session is recorded from Muse's own log, so this runtime emits no
/// transcript.
///
/// <para>The ready barrier (<see cref="WaitForSessionReadyAsync"/>) resolves only once the host has
/// answered <c>session/start</c> or <c>session/resume</c>, and faults on every other ending, the deadline
/// included; a factory must await it before handing the runtime to the orchestrator.</para>
///
/// <para><see cref="ReadOutputAsync"/> parks on one constructor-owned signal completed on entry to
/// terminal; its ending is what drives the orchestrator's finalize.</para>
/// </summary>
internal sealed class MuseMspHostedAgentRuntime : IHostedAgentRuntime, ITerminationVerdictSource {
    internal static readonly TimeSpan DefaultReadyDeadline = TimeSpan.FromSeconds(30);

    static readonly TimeSpan StopGrace        = TimeSpan.FromSeconds(3);
    static readonly TimeSpan SteerFallbackCap = TimeSpan.FromSeconds(30);
    static readonly TimeSpan DisposeJoinCap   = TimeSpan.FromSeconds(5);

    const string ProviderId       = "meta";
    const string DeclinedFeedback = "kcap: declined";

    readonly IMuseMspProcess _process;
    readonly ILogger         _logger;
    readonly string          _agentId;
    readonly string          _cwd;
    readonly string?         _requestedModel;
    readonly TimeProvider    _time;
    readonly MuseLaunchPlan  _plan;
    readonly string          _daemonVersion;
    readonly TimeSpan        _readyDeadline;
    readonly Action?         _onDisposed;
    readonly Func<AcpInteractionRequest, CancellationToken, Task<AcpInteractionDecision>> _requestInteraction;

    /// <summary>Never disposed: its token reaches process calls still in flight on other threads when
    /// disposal runs, and reading or registering on a disposed source throws.</summary>
    readonly CancellationTokenSource _ownerCts = new();
    readonly CancellationToken       _ownerToken;

    readonly TaskCompletionSource _terminalTcs  = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource _sessionReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Requests awaiting their response, faulted wholesale on terminal. The optional hook runs
    /// on the pump before the waiter wakes, so state it applies is ordered with later notifications.</summary>
    readonly ConcurrentDictionary<long, (TaskCompletionSource<MuseMspFrame> Waiter, Action<MuseMspFrame>? OnPump)> _pending = new();

    /// <summary>Serializes user input so a steer-or-start decision is never made against a turn another
    /// send is still opening. Never disposed, for the same reason as <see cref="_ownerCts"/>.</summary>
    readonly SemaphoreSlim _inputLock = new(1, 1);

    /// <summary>Open approval and question prompts by approval or user-input id.</summary>
    readonly ConcurrentDictionary<string, CancellationTokenSource> _prompts = new();

    /// <summary>The latest full view of each open approval; <c>approval/updated</c> carries only part of it.</summary>
    readonly ConcurrentDictionary<string, JsonElement> _approvalViews = new();

    /// <summary>The requirement each approval was last decided for, so the update our own decision
    /// provokes is not decided a second time.</summary>
    readonly ConcurrentDictionary<string, JsonElement> _decidedRequirements = new();

    readonly ConcurrentDictionary<Task, byte> _background = new();

    readonly Lock                       _turnGate    = new();
    readonly List<TaskCompletionSource> _idleWaiters = [];
    string? _runningTurnId;
    string? _lastCompletedTurnId;

    readonly ReapVerdictGate _gate;

    readonly Task _pumpTask;
    readonly Task _handshakeTask;

    long _requestSeq;
    int  _terminal;
    int  _disposed;
    int  _turnSucceeded;

    volatile MuseSessionDescriptor? _session;
    volatile MuseHostHandshake?     _handshake;
    volatile MuseLiveUsage?         _lastUsage;
    volatile string?                _resolvedModel;
    volatile bool                   _sessionClosedSeen;

    public MuseMspHostedAgentRuntime(
            IMuseMspProcess process,
            ILogger         logger,
            string          agentId,
            string          cwd,
            string?         requestedModel,
            TimeProvider    time,
            Func<AcpInteractionRequest, CancellationToken, Task<AcpInteractionDecision>> requestInteraction,
            MuseLaunchPlan  plan,
            string          daemonVersion,
            TimeSpan?       readyDeadline = null,
            Action?         onDisposed    = null) {
        _process            = process;
        _logger             = logger;
        _agentId            = agentId;
        _cwd                = cwd;
        _requestedModel     = requestedModel;
        _time               = time;
        _requestInteraction = requestInteraction;
        _plan               = plan;
        _daemonVersion      = daemonVersion;
        _readyDeadline      = readyDeadline ?? DefaultReadyDeadline;
        _onDisposed         = onDisposed;
        _ownerToken         = _ownerCts.Token;

        _gate = new ReapVerdictGate(() => Volatile.Read(ref _turnSucceeded) == 0, logger);

        // The pump starts first: the host can answer before the handshake's write continuation resumes.
        _pumpTask      = RunPumpAsync();
        _handshakeTask = RunHandshakeAsync();
    }

    public string Vendor              => "muse";
    public string RuntimeTransport    => "msp";
    public int    Pid                 => _process.Pid;
    public bool   HasExited           => _process.HasExited;
    public int?   ExitCode            => _process.ExitCode;
    public bool   EmitsTerminalOutput => false;

    /// <summary>The model the host reported for the session, then any <c>session/modelChanged</c>; never the
    /// merely requested model.</summary>
    public string? ResolvedModel => _resolvedModel;

    public bool SessionClosedSeen => _sessionClosedSeen;

    internal MuseSessionDescriptor? Session   => _session;
    internal MuseHostHandshake?     Handshake => _handshake;
    internal MuseLiveUsage?         LastUsage => _lastUsage;

    internal AgentActivityClock? ActivityClock { get; set; }

    internal Task WaitForSessionReadyAsync(CancellationToken ct) => _sessionReady.Task.WaitAsync(ct);

    public TerminationVerdict? ReadVerdict() => _gate.ReadVerdict();

    public bool TryInitiateNonFailureStatusSend(Func<Task> send, out Task sendTask) =>
        _gate.TryInitiateNonFailureStatusSend(send, out sendTask);

    // ---- Requests ----

    TaskCompletionSource<MuseMspFrame> RegisterPending(long id, Action<MuseMspFrame>? onPump) {
        var waiter = new TaskCompletionSource<MuseMspFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = (waiter, onPump);

        // EnterTerminal faults a snapshot of the keys; re-checking after the entry is visible covers an
        // entry added after that snapshot.
        if (Volatile.Read(ref _terminal) != 0) FaultPending(id);

        return waiter;
    }

    void FaultPending(long id) {
        if (_pending.TryRemove(id, out var entry))
            entry.Waiter.TrySetException(new InvalidOperationException($"Muse: the hosted session ended before request {id} was answered."));
    }

    /// <summary>Writes one request and returns its response, which the caller disposes. <paramref name="written"/>
    /// settles when the write does; <paramref name="remaining"/>, when given, bounds both the write and the wait.</summary>
    async Task<MuseMspFrame> RequestAsync(
            Func<long, string>        build,
            TaskCompletionSource?     written   = null,
            Action<MuseMspFrame>?     onPump    = null,
            Func<TimeSpan>?           remaining = null) {
        var id     = Interlocked.Increment(ref _requestSeq);
        var waiter = RegisterPending(id, onPump);

        try {
            if (Volatile.Read(ref _terminal) != 0)
                throw new InputNotAdmittedException("Muse: the hosted session has ended.");

            var write = _process.WriteLineAsync(build(id), _ownerToken);
            if (remaining is null) {
                await write.ConfigureAwait(false);
            } else {
                ObserveFault(write);
                await write.WaitAsync(remaining(), _time).ConfigureAwait(false);
            }

            written?.TrySetResult();
        } catch (Exception ex) {
            written?.TrySetException(ex);
            _pending.TryRemove(id, out _);
            throw;
        }

        try {
            return remaining is null
                ? await waiter.Task.ConfigureAwait(false)
                : await waiter.Task.WaitAsync(remaining(), _time).ConfigureAwait(false);
        } finally {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Sends a request whose answer nobody waits for; the returned task settles with the write.</summary>
    Task SendWithoutWaitingAsync(Func<long, string> build, string what) {
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Track(DiscardResponseAsync(RequestAsync(build, written), what));
        return written.Task;
    }

    async Task DiscardResponseAsync(Task<MuseMspFrame> response, string what) {
        using var frame = await response.ConfigureAwait(false);
        if (ErrorMessage(frame) is { } error)
            _logger.LogDebug("Muse: {What} was refused: {Error} (agentId={AgentId}).", what, error, _agentId);
    }

    static string? ErrorMessage(MuseMspFrame frame) =>
        frame.Error is { ValueKind: JsonValueKind.Object } error ? error.Str("message") ?? error.GetRawText() : null;

    void Track(Task task) {
        _background[task] = 0;
        task.ContinueWith(t => {
            _background.TryRemove(t, out _);
            if (t.IsFaulted)
                _logger.LogDebug(t.Exception, "Muse: a background task ended with an error (agentId={AgentId}).", _agentId);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    static void ObserveFault(Task task) =>
        task.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    // ---- Handshake ----

    async Task RunHandshakeAsync() {
        var started = _time.GetTimestamp();

        TimeSpan Remaining() {
            var left = _readyDeadline - _time.GetElapsedTime(started);
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }

        try {
            using (var init = await RequestAsync(id => MuseMsp.Initialize(id, _daemonVersion), remaining: Remaining).ConfigureAwait(false)) {
                if (ErrorMessage(init) is { } error)
                    throw new InvalidOperationException($"Muse: initialize was refused: {error}");

                var result  = init.Result is { ValueKind: JsonValueKind.Object } r ? r : default;
                var schema  = result.Obj("schema");
                var version = schema?.Num("version");

                if (version != 1) {
                    FaultSessionReadyIfUnresolved(
                        $"muse_schema_version_unsupported: host schema version {version?.ToString() ?? "none"}, this daemon speaks 1",
                        inner: null);
                    return;
                }

                _handshake = new MuseHostHandshake(1, schema?.Str("fingerprint"), Capabilities(result), result.Str("sandboxAvailability"));

                _logger.LogInformation("Muse: host schema fingerprint {Fingerprint} (agentId={AgentId}).",
                    _handshake.Fingerprint ?? "none", _agentId);
            }

            var initialized = _process.WriteLineAsync(MuseMsp.Initialized(), _ownerToken);
            ObserveFault(initialized);
            await initialized.WaitAsync(Remaining(), _time).ConfigureAwait(false);

            using var opened = await RequestAsync(BuildSessionRequest, remaining: Remaining).ConfigureAwait(false);

            if (ErrorMessage(opened) is { } refused)
                throw new InvalidOperationException($"Muse: the session was refused: {refused}");

            ApplySession(opened);
        } catch (Exception ex) {
            FaultSessionReadyIfUnresolved($"Muse: the hosted session's handshake failed (agentId={_agentId}).", ex);
        }
    }

    string BuildSessionRequest(long id) =>
        _plan.ResumeSessionId is { } resume
            ? MuseMsp.SessionResume(id, MuseMsp.NewId(), resume)
            : MuseMsp.SessionStart(
                id,
                MuseMsp.NewId(),
                _plan.MintedSessionId,
                _cwd,
                _requestedModel,
                ProviderId,
                _plan.PermissionProfile,
                _plan.ApprovalMode,
                _plan.McpServers?.DeepClone().AsObject());

    static IReadOnlySet<string> Capabilities(JsonElement result) {
        if (result.Arr("grantedCapabilities") is not { } arr || arr.GetArrayLength() == 0) return FrozenSet<string>.Empty;

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in arr.EnumerateArray())
            if (item.IsString) set.Add(item.GetString()!);

        return set.ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>Fails closed on a session with no id or log path: the recording correlates on both.</summary>
    void ApplySession(MuseMspFrame response) {
        var session = response.Result is { ValueKind: JsonValueKind.Object } r ? r.Obj("session") : null;

        if (session?.Str("sessionId") is not { Length: > 0 } sessionId || session.Value.Str("path") is not { Length: > 0 } path)
            throw new InvalidOperationException("Muse: the session answer carried no session id or log path.");

        if (_plan.ResumeSessionId is null && sessionId != _plan.MintedSessionId)
            _logger.LogWarning("Muse: the host started session {SessionId}, not the minted {Minted} (agentId={AgentId}).",
                sessionId, _plan.MintedSessionId, _agentId);

        var modelId = session.Value.Str("modelId");
        _session       = new MuseSessionDescriptor(sessionId, path, modelId, session.Value.Str("workspaceRoot") ?? _cwd);
        _resolvedModel = modelId;

        if (session.Value.Str("activeTurnId") is { Length: > 0 } active) SetRunningTurn(active);

        ActivityClock?.SetLaunchStage("session_created");

        _sessionReady.TrySetResult();
    }

    void FaultSessionReadyIfUnresolved(string message, Exception? inner) {
        if (!_sessionReady.TrySetException(inner is null ? new InvalidOperationException(message) : new InvalidOperationException(message, inner)))
            return;

        _logger.LogWarning(inner, "{Message}", message);
        ObserveFault(_sessionReady.Task);
    }

    // ---- Read pump ----

    async Task RunPumpAsync() {
        try {
            await foreach (var line in _process.ReadLinesAsync(_ownerToken).ConfigureAwait(false)) {
                var frame = MuseMspFrame.TryParse(line);
                if (frame is null) continue;

                if (frame.IsResponse) {
                    CompletePending(frame);
                    continue;
                }

                try {
                    if (frame.IsNotification) Dispatch(frame.Method!, frame.Params ?? default);
                    else if (frame is { Id: { } requestId, Method: "approval/request" or "userInput/request" }) WriteReceipt(requestId);
                } catch (Exception ex) {
                    _logger.LogDebug(ex, "Muse: failed to handle {Method} (agentId={AgentId}).", frame.Method, _agentId);
                } finally {
                    frame.Dispose();
                }
            }
        } catch (OperationCanceledException) when (_ownerToken.IsCancellationRequested) {
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Muse: the read pump ended unexpectedly (agentId={AgentId}).", _agentId);
        } finally {
            EnterTerminal();
        }
    }

    void CompletePending(MuseMspFrame frame) {
        if (!_pending.TryRemove(frame.Id!.Value, out var entry)) {
            frame.Dispose();
            return;
        }

        try {
            entry.OnPump?.Invoke(frame);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Muse: failed to apply response {Id} (agentId={AgentId}).", frame.Id, _agentId);
        }

        if (!entry.Waiter.TrySetResult(frame)) frame.Dispose();
    }

    void WriteReceipt(long id) {
        var write = _process.WriteLineAsync(MuseMsp.RequestReceipt(id), _ownerToken);
        Track(write);
    }

    void Dispatch(string method, JsonElement p) {
        if (method.StartsWith("item/", StringComparison.Ordinal)) {
            ActivityClock?.Advance();
            return;
        }

        switch (method) {
            case "turn/started":
                if (p.Str("turnId") is { Length: > 0 } started) SetRunningTurn(started);
                break;

            case "turn/completed":
                OnTurnCompleted(p);
                break;

            case "session/statusChanged" when p.Str("status") == "idle":
                ClearRunningTurn(onlyIf: null);
                break;

            case "approval/requested":
                OnApprovalView(p.Clone(), merge: false);
                break;

            case "approval/updated":
                OnApprovalView(p, merge: true);
                break;

            case "approval/resolved":
                if (p.Str("approvalId") is { } resolved) {
                    CancelPrompt(resolved);
                    _approvalViews.TryRemove(resolved, out _);
                    _decidedRequirements.TryRemove(resolved, out _);
                }
                break;

            case "userInput/requested":
                OnUserInputRequested(p.Clone());
                break;

            case "userInput/settled":
                if (p.Str("userInputId") is { } settled) CancelPrompt(settled);
                break;

            case "session/tokenUsage":
                var usage = p.Obj("usage") ?? p;
                _lastUsage = new MuseLiveUsage(
                    p.Str("modelId"),
                    usage.Num("inputTokens") ?? 0,
                    usage.Num("outputTokens") ?? 0,
                    usage.Num("cachedTokens") ?? 0,
                    usage.Num("reasoningTokens") ?? 0);
                break;

            case "session/modelChanged":
                if (p.Str("modelId") is { Length: > 0 } model) _resolvedModel = model;
                break;

            case "session/closed":
                _sessionClosedSeen = true;
                EnterTerminal();
                break;
        }
    }

    void OnTurnCompleted(JsonElement p) {
        var turnId   = p.Str("turnId");
        var terminal = p.Str("terminal");

        lock (_turnGate) _lastCompletedTurnId = turnId;
        ClearRunningTurn(onlyIf: turnId);

        if (_plan.IsReviewer
         && terminal == "failed"
         && p.Obj("error") is { } error
         && IsModelFailure(error.GetRawText()))
            _gate.TryStartReap($"muse_reviewer_model_failed: {error.Str("message") ?? error.GetRawText()}", RunSelfStopAsync);

        if (terminal == "completed") Interlocked.Exchange(ref _turnSucceeded, 1);
    }

    static bool IsModelFailure(string errorJson) =>
        errorJson.Contains("429", StringComparison.Ordinal)
     || errorJson.Contains("401", StringComparison.Ordinal)
     || errorJson.Contains("rate_limit", StringComparison.OrdinalIgnoreCase)
     || errorJson.Contains("auth", StringComparison.OrdinalIgnoreCase);

    async Task RunSelfStopAsync() {
        // The gate runs this under its lock and publishes the verdict when it returns.
        await Task.Yield();

        _logger.LogWarning("Muse: ending the unattended reviewer after a model failure (agentId={AgentId}).", _agentId);

        await RequestGracefulStopAsync().ConfigureAwait(false);
        if (!_process.HasExited) await TerminateAsync(StopGrace).ConfigureAwait(false);
    }

    // ---- Turn tracking ----

    void SetRunningTurn(string turnId) {
        lock (_turnGate) _runningTurnId = turnId;
        ActivityClock?.SetTurnInFlight(true);
    }

    /// <summary>Clears the running turn when it is <paramref name="onlyIf"/> (any turn when null) and releases
    /// idle waiters.</summary>
    void ClearRunningTurn(string? onlyIf) {
        TaskCompletionSource[] release;
        bool                   wasRunning;

        lock (_turnGate) {
            if (onlyIf is not null && _runningTurnId is { } running && running != onlyIf) return;

            wasRunning     = _runningTurnId is not null;
            _runningTurnId = null;
            release        = [.. _idleWaiters];
            _idleWaiters.Clear();
        }

        if (wasRunning) ActivityClock?.SetTurnInFlight(false);

        foreach (var waiter in release) waiter.TrySetResult();
    }

    string? RunningTurnId {
        get { lock (_turnGate) return _runningTurnId; }
    }

    /// <summary>A started turn's ack precedes its <c>turn/started</c>, so it marks the turn running at once;
    /// otherwise an idle wait right after the send would return before the turn began.</summary>
    void ApplyTurnStartAck(MuseMspFrame response) {
        if (response.Result is not { ValueKind: JsonValueKind.Object } result
         || result.Str("disposition") != "started"
         || result.Str("turnId") is not { Length: > 0 } turnId)
            return;

        lock (_turnGate) {
            if (_runningTurnId is not null || turnId == _lastCompletedTurnId) return;
            _runningTurnId = turnId;
        }

        ActivityClock?.SetTurnInFlight(true);
    }

    public Task WaitForTurnIdleAsync(CancellationToken ct) {
        TaskCompletionSource waiter;

        lock (_turnGate) {
            if (_runningTurnId is null) return Task.CompletedTask;

            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _idleWaiters.Add(waiter);
        }

        return ct.CanBeCanceled ? AwaitTurnIdleAsync(waiter, ct) : waiter.Task;
    }

    async Task AwaitTurnIdleAsync(TaskCompletionSource waiter, CancellationToken ct) {
        try {
            await waiter.Task.WaitAsync(ct).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            lock (_turnGate) _idleWaiters.Remove(waiter);
            throw;
        }
    }

    // ---- Input ----

    public Task SendUserInputAsync(string text) {
        if (Volatile.Read(ref _terminal) != 0) throw new InputNotAdmittedException("Muse: the hosted session has ended.");

        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Track(DeliverAsync(text, written));
        return written.Task;
    }

    /// <summary>Completes on the host's answer to the input, not merely its write.</summary>
    public Task SendUserInputAndWaitForWriteAsync(string text) =>
        Volatile.Read(ref _terminal) != 0
            ? Task.FromException(new InputNotAdmittedException("Muse: the hosted session has ended."))
            : DeliverAsync(text, written: null);

    async Task DeliverAsync(string text, TaskCompletionSource? written) {
        try {
            await _sessionReady.Task.WaitAsync(_ownerToken).ConfigureAwait(false);
            await _inputLock.WaitAsync(_ownerToken).ConfigureAwait(false);
        } catch (Exception ex) {
            written?.TrySetException(ex);
            throw;
        }

        try {
            var sessionId = _session!.SessionId;

            if (RunningTurnId is { } turnId) {
                using var steer = await RequestAsync(
                    id => MuseMsp.TurnSteer(id, MuseMsp.NewId(), sessionId, turnId, text), written).ConfigureAwait(false);

                if (ErrorMessage(steer) is not { } refused) return;

                _logger.LogInformation("Muse: steer into turn {TurnId} was refused ({Error}); starting a new turn (agentId={AgentId}).",
                    turnId, refused, _agentId);

                try {
                    await WaitForTurnIdleAsync(_ownerToken).WaitAsync(SteerFallbackCap, _time).ConfigureAwait(false);
                } catch (TimeoutException) {
                    _logger.LogDebug("Muse: the refused steer's turn did not settle in time (agentId={AgentId}).", _agentId);
                }
            }

            using var start = await RequestAsync(
                id => MuseMsp.TurnStart(id, MuseMsp.NewId(), sessionId, text), written, ApplyTurnStartAck).ConfigureAwait(false);

            if (ErrorMessage(start) is { } error) {
                _logger.LogWarning("Muse: the input was not accepted: {Error} (agentId={AgentId}).", error, _agentId);
                throw new InvalidOperationException($"Muse: the input was not accepted: {error}");
            }
        } finally {
            _inputLock.Release();
        }
    }

    public async Task SendSpecialKeyAsync(string key) {
        if (!string.Equals(key, "escape", StringComparison.OrdinalIgnoreCase)) {
            _logger.LogDebug("Muse runtime ignoring SendSpecialKeyAsync({Key}).", key);
            return;
        }

        try {
            await CancelRunningTurnAsync().ConfigureAwait(false);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Muse: failed to cancel the running turn (agentId={AgentId}).", _agentId);
        }
    }

    Task CancelRunningTurnAsync() =>
        RunningTurnId is { } turnId && _session is { } session
            ? SendWithoutWaitingAsync(id => MuseMsp.TurnCancel(id, MuseMsp.NewId(), session.SessionId, turnId), "turn/cancel")
            : Task.CompletedTask;

    public Task SendRawInputAsync(byte[] data) =>
        throw new NotSupportedException("Local-attach raw input is a PTY-only surface; the Muse runtime has no terminal.");

    public void Resize(ushort cols, ushort rows) { }

    // ---- Approvals and questions ----

    void OnApprovalView(JsonElement view, bool merge) {
        if (view.Str("approvalId") is not { } approvalId) return;

        if (merge) view = _approvalViews.TryGetValue(approvalId, out var prior) ? Merge(prior, view) : view.Clone();

        _approvalViews[approvalId] = view;

        if (view.Prop("currentRequirementId") is { } requirement
         && _decidedRequirements.TryGetValue(approvalId, out var decided)
         && JsonElement.DeepEquals(decided, requirement))
            return;

        StartPrompt(approvalId, ct => HandleApprovalAsync(approvalId, view, ct));
    }

    /// <summary>The prior view with every member the update carries replaced.</summary>
    static JsonElement Merge(JsonElement prior, JsonElement update) {
        var merged = JsonNode.Parse(prior.GetRawText())!.AsObject();
        foreach (var member in update.EnumerateObject()) merged[member.Name] = JsonNode.Parse(member.Value.GetRawText());

        using var doc = JsonDocument.Parse(merged.ToJsonString());
        return doc.RootElement.Clone();
    }

    void OnUserInputRequested(JsonElement p) {
        if (p.Str("userInputId") is not { } userInputId) return;

        StartPrompt(userInputId, ct => HandleUserInputAsync(userInputId, p, ct));
    }

    void StartPrompt(string key, Func<CancellationToken, Task> handler) {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_ownerToken);

        if (_prompts.TryRemove(key, out var superseded)) CancelQuietly(superseded);
        _prompts[key] = cts;

        Track(RunPromptAsync(key, cts, handler));
    }

    async Task RunPromptAsync(string key, CancellationTokenSource cts, Func<CancellationToken, Task> handler) {
        // Off the pump: a handler may wait on a human for as long as they take.
        await Task.Yield();

        try {
            await handler(cts.Token).ConfigureAwait(false);
        } finally {
            _prompts.TryRemove(new KeyValuePair<string, CancellationTokenSource>(key, cts));
            cts.Dispose();
        }
    }

    void CancelPrompt(string key) {
        if (_prompts.TryRemove(key, out var cts)) CancelQuietly(cts);
    }

    static void CancelQuietly(CancellationTokenSource cts) {
        try {
            cts.Cancel();
        } catch (ObjectDisposedException) {
            // Its handler already finished.
        }
    }

    async Task HandleApprovalAsync(string approvalId, JsonElement view, CancellationToken ct) {
        MuseApprovalDecision decision;

        try {
            if (_plan.IsReviewer) {
                decision = MuseUnattendedApprovals.Decide(view, _plan.AdmittedTools);
            } else {
                var answer = await _requestInteraction(MuseApprovalBridge.ToInteraction(_agentId, LaunchSessionId, view), ct).ConfigureAwait(false);
                decision = MuseApprovalBridge.ToDecision(answer, view);
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            return;
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Muse: approval {ApprovalId} could not be decided; declining it (agentId={AgentId}).", approvalId, _agentId);
            decision = new MuseApprovalDecision(MuseApprovalBridge.AbortChoiceId(MuseApprovalBridge.Choices(view)), DeclinedFeedback);
        }

        // Resolved by the host, superseded by an update, or torn down while the human was deciding.
        if (ct.IsCancellationRequested) return;

        if (view.Prop("currentRequirementId") is not { } requirement) {
            _logger.LogWarning("Muse: approval {ApprovalId} carried no requirement id (agentId={AgentId}).", approvalId, _agentId);
            return;
        }

        // Recorded before the write: the update our own decision provokes can arrive before its response.
        _decidedRequirements[approvalId] = requirement;

        try {
            using var response = await RequestAsync(id => MuseMsp.ApprovalDecide(
                id, MuseMsp.NewId(), SessionIdOr(view), approvalId, requirement, decision.ChoiceId, decision.Feedback)).ConfigureAwait(false);

            if (ErrorMessage(response) is { } error)
                _logger.LogWarning("Muse: the decision on approval {ApprovalId} was refused: {Error} (agentId={AgentId}).", approvalId, error, _agentId);
        } catch {
            _decidedRequirements.TryRemove(approvalId, out _);
            throw;
        }
    }

    async Task HandleUserInputAsync(string userInputId, JsonElement p, CancellationToken ct) {
        IReadOnlyList<MuseUserInputAnswer>? answers = null;

        if (_plan.IsReviewer) {
            _logger.LogInformation("Muse: cancelling question {UserInputId}: the unattended reviewer has nobody to ask (agentId={AgentId}).",
                userInputId, _agentId);
        } else {
            try {
                var answer = await _requestInteraction(MuseUserInputBridge.ToInteraction(_agentId, LaunchSessionId, p), ct).ConfigureAwait(false);
                answers = MuseUserInputBridge.ToAnswers(answer, p);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                return;
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Muse: question {UserInputId} could not be asked; cancelling it (agentId={AgentId}).", userInputId, _agentId);
            }

            if (ct.IsCancellationRequested) return;
        }

        var sessionId = SessionIdOr(p);

        using var response = await RequestAsync(id => answers is null
            ? MuseMsp.UserInputCancel(id, MuseMsp.NewId(), sessionId, userInputId)
            : MuseMsp.UserInputAnswer(id, MuseMsp.NewId(), sessionId, userInputId, answers)).ConfigureAwait(false);

        if (ErrorMessage(response) is { } error)
            _logger.LogWarning("Muse: the reply to question {UserInputId} was refused: {Error} (agentId={AgentId}).", userInputId, error, _agentId);
    }

    /// <summary>The session the interaction route knows this launch by.</summary>
    string LaunchSessionId => _session?.SessionId ?? _plan.MintedSessionId;

    /// <summary>A reply goes to the session the request names; a child approval names the parent.</summary>
    string SessionIdOr(JsonElement p) => p.Str("sessionId") ?? LaunchSessionId;

    // ---- Output ----

    public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default) {
        await _terminalTcs.Task.WaitAsync(ct).ConfigureAwait(false);
        yield break;
    }

    // ---- Terminal and teardown ----

    void EnterTerminal() {
        if (Interlocked.Exchange(ref _terminal, 1) != 0) return;

        _gate.Seal();

        _terminalTcs.TrySetResult();

        foreach (var id in _pending.Keys) FaultPending(id);

        FaultSessionReadyIfUnresolved($"Muse: the hosted session ended before its handshake completed (agentId={_agentId}).", inner: null);

        foreach (var key in _prompts.Keys) CancelPrompt(key);

        ClearRunningTurn(onlyIf: null);
    }

    /// <summary>Cancels a running turn, closes stdin, and waits for <c>session/closed</c> or exit. Best-effort:
    /// the orchestrator terminates afterwards when the host has not gone.</summary>
    public async Task RequestGracefulStopAsync() {
        var started = _time.GetTimestamp();

        TimeSpan Remaining() {
            var left = StopGrace - _time.GetElapsedTime(started);
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }

        try {
            await CancelRunningTurnAsync().WaitAsync(Remaining(), _time).ConfigureAwait(false);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Muse: the graceful stop's turn/cancel was not written (agentId={AgentId}).", _agentId);
        }

        try {
            await _process.CloseInputAsync(StopGrace).ConfigureAwait(false);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Muse: failed to close the host's stdin (agentId={AgentId}).", _agentId);
        }

        // session/closed and the host's stdout ending both enter terminal.
        try {
            await _terminalTcs.Task.WaitAsync(Remaining(), _time).ConfigureAwait(false);
        } catch (TimeoutException) {
            _logger.LogDebug("Muse: the host did not close within the stop grace (agentId={AgentId}).", _agentId);
            return;
        }

        if (!_process.HasExited) await _process.WaitForExitAsync(Remaining()).ConfigureAwait(false);
    }

    public Task WaitForExitAsync(TimeSpan? timeout = null) => _process.WaitForExitAsync(timeout);

    public async Task TerminateAsync(TimeSpan? timeout = null) {
        EnterTerminal();

        await _ownerCts.CancelAsync().ConfigureAwait(false);

        try {
            await _process.TerminateAsync(timeout ?? StopGrace).ConfigureAwait(false);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Muse: failed to terminate the host (agentId={AgentId}).", _agentId);
        }
    }

    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        EnterTerminal();

        await _ownerCts.CancelAsync().ConfigureAwait(false);

        if (!_process.HasExited) {
            try {
                await _process.TerminateAsync(StopGrace).ConfigureAwait(false);
            } catch (Exception ex) {
                _logger.LogDebug(ex, "Muse: failed to terminate the host at dispose (agentId={AgentId}).", _agentId);
            }
        }

        var joins = new List<Task>(_background.Keys) { _pumpTask, _handshakeTask };
        if (_gate.Seal() is { } selfStop) joins.Add(selfStop);

        try {
            await Task.WhenAll(joins).WaitAsync(DisposeJoinCap, _time).ConfigureAwait(false);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Muse: background work did not finish within the dispose budget (agentId={AgentId}).", _agentId);
        }

        try {
            await _process.DisposeAsync().ConfigureAwait(false);
        } catch (Exception ex) {
            _logger.LogDebug(ex, "Muse: failed to dispose the host (agentId={AgentId}).", _agentId);
        }

        if (_onDisposed is null) return;

        try {
            _onDisposed();
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Muse: the runtime's disposal callback failed (agentId={AgentId}).", _agentId);
        }
    }
}
