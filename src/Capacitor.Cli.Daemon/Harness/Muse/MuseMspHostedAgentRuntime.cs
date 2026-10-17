using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
internal sealed partial class MuseMspHostedAgentRuntime : IHostedAgentRuntime, ITerminationVerdictSource {
    internal static readonly TimeSpan DefaultReadyDeadline = TimeSpan.FromSeconds(30);

    static readonly TimeSpan StopGrace        = TimeSpan.FromSeconds(3);
    static readonly TimeSpan SteerFallbackCap = TimeSpan.FromSeconds(30);
    static readonly TimeSpan DisposeJoinCap   = TimeSpan.FromSeconds(5);

    const string ProviderId       = "meta";
    const string DeclinedFeedback = "kcap: declined";

    const int ApprovalAlreadyResolvedCode = -32051;

    readonly IMuseMspProcess _process;
    readonly ILogger         _logger;
    readonly string          _agentId;
    readonly string          _cwd;
    readonly string?         _requestedModel;
    readonly TimeProvider    _time;
    readonly MuseLaunchPlan  _plan;
    readonly string          _daemonVersion;
    readonly TimeSpan        _readyDeadline;
    readonly Func<Task>?     _onDisposed;
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

    /// <summary>Woken on every change of the running turn, a start included, unlike the idle waiters.</summary>
    readonly List<TaskCompletionSource> _turnChangeWaiters = [];
    string? _runningTurnId;
    string? _lastCompletedTurnId;

    /// <summary>The steer awaiting proof that its turn took it: the user-message item carrying its command id.</summary>
    (string CommandId, TaskCompletionSource Taken)? _pendingSteer;

    /// <summary>Set once this host has shown a user-message item naming its command, which is what a dropped
    /// steer is told apart by; a host that never shows one cannot be judged that way.</summary>
    volatile bool _hostEchoesUserMessages;

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
            Func<Task>?     onDisposed    = null) {
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

    /// <summary>The server accepts only <c>pty</c> or <c>app-server</c> here and expects <c>pty</c> for
    /// every vendor but Codex; anything else fails the registration.</summary>
    public string RuntimeTransport    => "pty";
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
        ObserveFault(waiter.Task);
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

    /// <summary>An input request's response, or null when the deadline passed with its write unfinished or
    /// its answer missing. Either way the line may still reach the host and run, so the input must not be
    /// reported dropped: a stdin write cannot be withdrawn, since cancelling it mid-line would corrupt the
    /// line-delimited stream. The request stays pending, so a late answer still applies, and a late write
    /// failure or refusal is logged. Only a write that fails or a refusal that arrives within the deadline
    /// throws. With <paramref name="requireAnswer"/>, the deadline throws too.</summary>
    async Task<MuseMspFrame?> RequestInputAsync(
            Func<long, string>    build,
            string                what,
            bool                  requireAnswer,
            Action<MuseMspFrame>? onPump = null) {
        var remaining = RequestDeadline();
        var id        = Interlocked.Increment(ref _requestSeq);
        var waiter    = RegisterPending(id, onPump);
        var write     = Task.CompletedTask;

        try {
            if (Volatile.Read(ref _terminal) != 0)
                throw new InputNotAdmittedException("Muse: the hosted session has ended.");

            write = _process.WriteLineAsync(build(id), _ownerToken);
            ObserveFault(write);
            await write.WaitAsync(remaining(), _time).ConfigureAwait(false);

            return await waiter.Task.WaitAsync(remaining(), _time).ConfigureAwait(false);
        } catch (TimeoutException) when (!requireAnswer && Volatile.Read(ref _terminal) == 0) {
            _logger.LogWarning("Muse: {What} was not written and answered within {Seconds}s; its acceptance is unconfirmed, so "
                             + "the input is reported delivered (agentId={AgentId}).", what, _readyDeadline.TotalSeconds, _agentId);
            Track(LogLateOutcomeAsync(id, write, waiter.Task, what));
            return null;
        } catch {
            _pending.TryRemove(id, out _);
            throw;
        }
    }

    async Task LogLateOutcomeAsync(long id, Task write, Task<MuseMspFrame> response, string what) {
        try {
            await write.ConfigureAwait(false);
        } catch (Exception ex) {
            _pending.TryRemove(id, out _);
            _logger.LogWarning(ex, "Muse: {What} failed to write after its deadline, so its input was not run (agentId={AgentId}).",
                what, _agentId);
            return;
        }

        using var frame = await response.ConfigureAwait(false);
        if (ErrorMessage(frame) is { } error)
            _logger.LogWarning("Muse: {What} was refused after its deadline, so its input was not run: {Error} (agentId={AgentId}).",
                what, error, _agentId);
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
        frame.Error is { } error && error.IsObject ? error.Str("message") ?? error.GetRawText() : null;

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

                var result  = init.Result ?? default;
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
        var session = response.Result?.Obj("session");

        if (session?.Str("sessionId") is not { Length: > 0 } sessionId || session.Value.Str("path") is not { Length: > 0 } path)
            throw new InvalidOperationException("Muse: the session answer carried no session id or log path.");

        // The hosted-session marker and the hook's agent link are keyed by the minted id.
        if (_plan.ResumeSessionId is null && !SameSessionId(sessionId, _plan.MintedSessionId)) {
            FaultSessionReadyIfUnresolved(
                $"muse_session_id_mismatch: the host started session {sessionId}, not the minted {_plan.MintedSessionId}",
                inner: null);
            return;
        }

        var modelId = session.Value.Str("modelId");
        _session       = new MuseSessionDescriptor(sessionId, path, modelId, session.Value.Str("workspaceRoot") ?? _cwd);
        _resolvedModel = modelId;

        if (session.Value.Str("activeTurnId") is { Length: > 0 } active) SetRunningTurn(active);

        ActivityClock?.SetLaunchStage("session_created");

        _sessionReady.TrySetResult();
    }

    static bool SameSessionId(string a, string b) =>
        Guid.TryParse(a, out var ga) && Guid.TryParse(b, out var gb) ? ga == gb : string.Equals(a, b, StringComparison.Ordinal);

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
            NoteSteeredItem(p);
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
         && IsModelFailure(error))
            _gate.TryStartReap($"muse_reviewer_model_failed: {error.Str("message") ?? error.GetRawText()}", RunSelfStopAsync);

        if (terminal == "completed") Interlocked.Exchange(ref _turnSucceeded, 1);
    }

    /// <summary>A typed quota detail or an <c>authRequired</c> kind decides it; the message is matched on
    /// whole words only, so "author" or a digit run inside a longer number is not a model failure.</summary>
    internal static bool IsModelFailure(JsonElement error) =>
        error.Obj("quota") is not null
     || error.Str("kind") == "authRequired"
     || (error.Str("message") is { } message && ModelFailureText().IsMatch(message));

    [GeneratedRegex(@"\b(?:401|429|auth|unauthorized|unauthenticated|authentication|authorization)\b|rate[_ ]?limit",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModelFailureText();

    async Task RunSelfStopAsync() {
        // The gate runs this under its lock and publishes the verdict when it returns.
        await Task.Yield();

        _logger.LogWarning("Muse: ending the unattended reviewer, which cannot continue (agentId={AgentId}).", _agentId);

        await RequestGracefulStopAsync().ConfigureAwait(false);
        if (!_process.HasExited) await TerminateAsync(StopGrace).ConfigureAwait(false);
    }

    // ---- Turn tracking ----

    /// <summary>Ignored after terminal: nothing would clear a turn marked running then, and an idle waiter
    /// would park forever.</summary>
    void SetRunningTurn(string turnId) {
        TaskCompletionSource[] changed;

        lock (_turnGate) {
            if (Volatile.Read(ref _terminal) != 0) return;
            _runningTurnId = turnId;
            changed        = TakeTurnChangeWaiters();
        }

        ActivityClock?.SetTurnInFlight(true);

        foreach (var waiter in changed) waiter.TrySetResult();
    }

    TaskCompletionSource[] TakeTurnChangeWaiters() {
        var waiters = _turnChangeWaiters.ToArray();
        _turnChangeWaiters.Clear();
        return waiters;
    }

    /// <summary>Clears the running turn when it is <paramref name="onlyIf"/> (any turn when null) and releases
    /// idle waiters.</summary>
    void ClearRunningTurn(string? onlyIf) {
        TaskCompletionSource[] release;
        TaskCompletionSource[] changed;
        bool                   wasRunning;

        lock (_turnGate) {
            // A turn ending behind the current one is still a change for a waiter on that turn.
            changed = TakeTurnChangeWaiters();

            if (onlyIf is not null && _runningTurnId is { } running && running != onlyIf) {
                release    = [];
                wasRunning = false;
            } else {
                wasRunning     = _runningTurnId is not null;
                _runningTurnId = null;
                release        = [.. _idleWaiters];
                _idleWaiters.Clear();
            }
        }

        if (wasRunning) ActivityClock?.SetTurnInFlight(false);

        foreach (var waiter in changed) waiter.TrySetResult();
        foreach (var waiter in release) waiter.TrySetResult();
    }

    string? RunningTurnId {
        get { lock (_turnGate) return _runningTurnId; }
    }

    /// <summary>A started turn's ack precedes its <c>turn/started</c>, so it marks the turn running at once;
    /// otherwise an idle wait right after the send would return before the turn began.</summary>
    void ApplyTurnStartAck(MuseMspFrame response) {
        var result = response.Result ?? default;

        if (result.Str("disposition") != "started"
         || result.Str("turnId") is not { Length: > 0 } turnId)
            return;

        lock (_turnGate) {
            if (Volatile.Read(ref _terminal) != 0 || _runningTurnId is not null || turnId == _lastCompletedTurnId) return;
            _runningTurnId = turnId;
        }

        ActivityClock?.SetTurnInFlight(true);
    }

    public Task WaitForTurnIdleAsync(CancellationToken ct) {
        TaskCompletionSource waiter;

        lock (_turnGate) {
            if (_runningTurnId is null || Volatile.Read(ref _terminal) != 0) return Task.CompletedTask;

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

    /// <summary>Completes on the host's acceptance of the input (the steer, or the turn a refused steer falls
    /// back to), or when a written request goes unanswered past the deadline, since the host may still run
    /// it. Faults on a refusal or a failed write.</summary>
    public Task SendUserInputAsync(string text) {
        if (Volatile.Read(ref _terminal) != 0) throw new InputNotAdmittedException("Muse: the hosted session has ended.");

        return DeliverAsync(text, requireAnswer: false);
    }

    public Task SendUserInputAndWaitForWriteAsync(string text) =>
        Volatile.Read(ref _terminal) != 0
            ? Task.FromException(new InputNotAdmittedException("Muse: the hosted session has ended."))
            : DeliverAsync(text, requireAnswer: false);

    /// <summary>As <see cref="SendUserInputAsync"/>, but an unanswered request faults with a
    /// <see cref="TimeoutException"/> at the deadline: a launch whose first prompt is never acknowledged fails.</summary>
    internal Task SendFirstPromptAsync(string text) =>
        Volatile.Read(ref _terminal) != 0
            ? Task.FromException(new InputNotAdmittedException("Muse: the hosted session has ended."))
            : DeliverAsync(text, requireAnswer: true);

    async Task DeliverAsync(string text, bool requireAnswer) {
        await _sessionReady.Task.WaitAsync(_ownerToken).ConfigureAwait(false);
        await _inputLock.WaitAsync(_ownerToken).ConfigureAwait(false);

        try {
            var sessionId = _session!.SessionId;

            if (RunningTurnId is { } turnId && await SteerAsync(sessionId, turnId, text, requireAnswer).ConfigureAwait(false))
                return;

            using var start = await RequestInputAsync(
                id => MuseMsp.TurnStart(id, MuseMsp.NewId(), sessionId, text), "turn/start", requireAnswer, ApplyTurnStartAck).ConfigureAwait(false);

            if (start is not null && ErrorMessage(start) is { } error) {
                _logger.LogWarning("Muse: the input was not accepted: {Error} (agentId={AgentId}).", error, _agentId);
                throw new InvalidOperationException($"Muse: the input was not accepted: {error}");
            }
        } catch (TimeoutException ex) {
            throw new TimeoutException($"Muse: the input was not written and answered within {_readyDeadline.TotalSeconds:0}s.", ex);
        } finally {
            _inputLock.Release();
        }
    }

    /// <summary>True once the turn has taken the steered input, or its fate cannot be confirmed; false when
    /// the turn refused it or ended without taking it, so the caller starts a new turn with the same text.
    /// The ack only admits a steer: a turn already winding down drops it afterwards, recording
    /// <c>turn_submit.rejected</c> in its own log and telling the client nothing, so only the user-message
    /// item carrying the steer's command id proves the input is in the turn. That judgement is made only
    /// against a host that has echoed such an item before; on any other host the ack stands, since a
    /// second turn could run the input twice.</summary>
    async Task<bool> SteerAsync(string sessionId, string turnId, string text, bool requireAnswer) {
        var commandId = MuseMsp.NewId();
        var taken     = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_turnGate) _pendingSteer = (commandId, taken);

        using var watch = CancellationTokenSource.CreateLinkedTokenSource(_ownerToken);

        try {
            using var steer = await RequestInputAsync(
                id => MuseMsp.TurnSteer(id, commandId, sessionId, turnId, text), "turn/steer", requireAnswer).ConfigureAwait(false);

            if (steer is null) return true;

            if (ErrorMessage(steer) is { } refused) {
                _logger.LogInformation("Muse: steer into turn {TurnId} was refused ({Error}); starting a new turn (agentId={AgentId}).",
                    turnId, refused, _agentId);
                await WaitForTurnToSettleAsync().ConfigureAwait(false);

                return false;
            }

            var ended = WaitForTurnEndAsync(turnId, watch.Token);

            try {
                await Task.WhenAny(taken.Task, ended).WaitAsync(SteerFallbackCap, _time).ConfigureAwait(false);
            } catch (TimeoutException) {
                _logger.LogWarning("Muse: steer into turn {TurnId} was admitted but not seen in the turn within {Seconds}s; the input is "
                                 + "reported delivered (agentId={AgentId}).", turnId, SteerFallbackCap.TotalSeconds, _agentId);
                return true;
            }

            if (taken.Task.IsCompleted) return true;

            if (Volatile.Read(ref _terminal) != 0 || !_hostEchoesUserMessages) {
                _logger.LogWarning("Muse: turn {TurnId} ended before the steered input was seen in it; the input is reported "
                                 + "delivered (agentId={AgentId}).", turnId, _agentId);
                return true;
            }

            _logger.LogInformation("Muse: turn {TurnId} ended without taking the steered input; starting a new turn (agentId={AgentId}).",
                turnId, _agentId);

            return false;
        } finally {
            watch.Cancel();
            lock (_turnGate) _pendingSteer = null;
        }
    }

    async Task WaitForTurnToSettleAsync() {
        try {
            await WaitForTurnIdleAsync(_ownerToken).WaitAsync(SteerFallbackCap, _time).ConfigureAwait(false);
        } catch (TimeoutException) {
            _logger.LogDebug("Muse: the refused steer's turn did not settle in time (agentId={AgentId}).", _agentId);
        }
    }

    /// <summary>Completes once <paramref name="turnId"/> is no longer the running turn, whichever turn runs
    /// after it; a wait on plain idleness would follow the next turn instead.</summary>
    async Task WaitForTurnEndAsync(string turnId, CancellationToken ct) {
        while (true) {
            TaskCompletionSource waiter;

            lock (_turnGate) {
                if (_runningTurnId != turnId || Volatile.Read(ref _terminal) != 0) return;

                waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _turnChangeWaiters.Add(waiter);
            }

            try {
                await waiter.Task.WaitAsync(ct).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                lock (_turnGate) _turnChangeWaiters.Remove(waiter);
                throw;
            }
        }
    }

    void NoteSteeredItem(JsonElement p) {
        if (p.Obj("item") is not { } item || item.Str("kind") != "userMessage" || item.Str("commandId") is not { } commandId) return;

        _hostEchoesUserMessages = true;

        TaskCompletionSource? taken = null;
        lock (_turnGate) {
            if (_pendingSteer is { } pending && pending.CommandId == commandId) taken = pending.Taken;
        }

        taken?.TrySetResult();
    }

    /// <summary>A fresh deadline for one request, so a host that never answers cannot hold the input lane
    /// or an approval.</summary>
    Func<TimeSpan> RequestDeadline() {
        var started = _time.GetTimestamp();

        return () => {
            var left = _readyDeadline - _time.GetElapsedTime(started);
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        };
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

        if (_plan.IsReviewer) {
            // Synchronous and never cancelled: the reviewer answers every approval it is shown.
            try {
                decision = MuseUnattendedApprovals.Decide(view, _plan.AdmittedTools);
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Muse: approval {ApprovalId} could not be decided; declining it (agentId={AgentId}).", approvalId, _agentId);
                decision = Declined(view);
            }
        } else {
            try {
                var answer = await _requestInteraction(MuseApprovalBridge.ToInteraction(_agentId, LaunchSessionId, view), ct).ConfigureAwait(false);
                decision = MuseApprovalBridge.ToDecision(answer, view);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                return;
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Muse: approval {ApprovalId} could not be decided; declining it (agentId={AgentId}).", approvalId, _agentId);
                decision = Declined(view);
            }
        }

        // Resolved by the host, superseded by an update, or torn down while the human was deciding.
        if (ct.IsCancellationRequested) return;

        if (view.Prop("currentRequirementId") is not { } requirement) {
            _logger.LogWarning("Muse: approval {ApprovalId} carried no requirement id (agentId={AgentId}).", approvalId, _agentId);
            return;
        }

        if (await DecideAsync(approvalId, view, requirement, decision).ConfigureAwait(false)) return;

        // An unattended reviewer's turn waits on the approval, and nobody else will resolve it.
        if (!_plan.IsReviewer || ct.IsCancellationRequested) return;

        if (await DecideAsync(approvalId, view, requirement, Declined(view)).ConfigureAwait(false)) return;

        _gate.TryStartReap(
            $"muse_reviewer_approval_refused: the Muse host did not take any decision on approval {approvalId}, so the reviewer cannot continue.",
            RunSelfStopAsync);
    }

    /// <summary>True when the host took the decision or had already resolved the approval; false when it
    /// refused it, left it unanswered past the deadline, or it could not be sent. The approval may then
    /// still be open, so its requirement is forgotten and a later update for it is decided again.</summary>
    async Task<bool> DecideAsync(string approvalId, JsonElement view, JsonElement requirement, MuseApprovalDecision decision) {
        // Recorded before the write: the update our own decision provokes can arrive before its response.
        _decidedRequirements[approvalId] = requirement;

        try {
            using var response = await RequestAsync(id => MuseMsp.ApprovalDecide(
                    id, MuseMsp.NewId(), SessionIdOr(view), approvalId, requirement, decision.ChoiceId, decision.Feedback),
                remaining: RequestDeadline()).ConfigureAwait(false);

            if (ErrorMessage(response) is not { } error) return true;

            // The host can settle an approval by policy or judge before our decision lands.
            if (IsAlreadyResolved(response)) {
                _logger.LogDebug("Muse: approval {ApprovalId} was already resolved by the host (agentId={AgentId}).", approvalId, _agentId);
                return true;
            }

            _decidedRequirements.TryRemove(approvalId, out _);
            _logger.LogWarning("Muse: the {Choice} decision on approval {ApprovalId} was refused: {Error} (agentId={AgentId}).",
                decision.ChoiceId, approvalId, error, _agentId);
            return false;
        } catch (Exception ex) {
            _decidedRequirements.TryRemove(approvalId, out _);

            if (Volatile.Read(ref _terminal) != 0)
                _logger.LogDebug(ex, "Muse: the decision on approval {ApprovalId} was not delivered (agentId={AgentId}).", approvalId, _agentId);
            else
                _logger.LogWarning(ex, "Muse: the {Choice} decision on approval {ApprovalId} was not taken (agentId={AgentId}).",
                    decision.ChoiceId, approvalId, _agentId);
            return false;
        }
    }

    static MuseApprovalDecision Declined(JsonElement view) {
        try {
            return new MuseApprovalDecision(MuseApprovalBridge.AbortChoiceId(MuseApprovalBridge.Choices(view)), DeclinedFeedback);
        } catch (Exception) {
            return new MuseApprovalDecision("abort", DeclinedFeedback);
        }
    }

    static bool IsAlreadyResolved(MuseMspFrame response) =>
        response.Error is { } error
     && (error.Num("code") == ApprovalAlreadyResolvedCode || error.Obj("data")?.Str("kind") == "approvalAlreadyResolved");

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

        if (answers is not null) {
            using var answered = await RequestAsync(id => MuseMsp.UserInputAnswer(id, MuseMsp.NewId(), sessionId, userInputId, answers)).ConfigureAwait(false);

            if (ErrorMessage(answered) is not { } refusal) return;

            // A refused answer leaves the question open, and the agent waits on it until someone cancels it.
            _logger.LogWarning("Muse: the answer to question {UserInputId} was refused: {Error}; cancelling it (agentId={AgentId}).",
                userInputId, refusal, _agentId);

            if (ct.IsCancellationRequested) return;
        }

        using var cancelled = await RequestAsync(id => MuseMsp.UserInputCancel(id, MuseMsp.NewId(), sessionId, userInputId)).ConfigureAwait(false);

        if (ErrorMessage(cancelled) is { } error)
            _logger.LogWarning("Muse: the cancel of question {UserInputId} was refused: {Error} (agentId={AgentId}).", userInputId, error, _agentId);
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
            await _onDisposed().ConfigureAwait(false);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Muse: the runtime's disposal callback failed (agentId={AgentId}).", _agentId);
        }
    }
}
