using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

internal partial class ServerConnection : IAsyncDisposable, IDaemonHeartbeatPort {
    readonly HubConnection             _hub;
    readonly DaemonConfig              _config;
    readonly ILogger<ServerConnection> _logger;

    // Events for incoming commands from server
    public event Func<LaunchAgentCommand, Task>?    OnLaunchAgent;
    public event Func<string, Task>?                OnStopAgent; // agentId
    public event Func<SendInputCommand, Task>?      OnSendInput;
    public event Func<string, string, Task>?        OnSendSpecialKey; // agentId, key
    public event Func<ResizeTerminalCommand, Task>? OnResizeTerminal;

    // Per-phase eval handlers (DEV-1463 PR 2). These use SignalR's
    // client-result invocation — the server calls
    // HubConnection.InvokeAsync<T> which expects exactly one handler
    // returning the typed result, so we use settable properties rather
    // than multicast events. The SignalR 10.0.5 On<T1, TResult> overloads
    // don't expose a CancellationToken to the handler (verified against
    // Microsoft.AspNetCore.SignalR.Client reflection), so per-call
    // cancellation has to be driven by the daemon's own shutdown token —
    // the handler implementations link their work to _shutdownToken.
    public Func<PrepareEvalCommand,  Task<PrepareResult>>?  PrepareEvalHandler  { get; set; }
    public Func<RunQuestionCommand,  Task<QuestionResult>>? RunQuestionHandler  { get; set; }
    public Func<FinalizeEvalCommand, Task<FinalizeResult>>? FinalizeEvalHandler { get; set; }
    public Func<CancelEvalCommand,   Task>?                 CancelEvalHandler   { get; set; }

    /// <summary>
    /// Handler for the server's "do you have a checkout of this repo?" probe.
    /// Receives <c>(owner, repo, candidatePaths)</c> and returns confirmed git
    /// roots. Set by <see cref="AgentOrchestrator"/> at startup.
    /// </summary>
    public Func<FindRepoForRemoteRequest, Task<string[]>>? FindRepoForRemoteHandler { get; set; }

    /// <summary>
    /// Callback invoked at <see cref="RegisterDaemon"/> time to snapshot the
    /// agent IDs currently hosted by this daemon. The server uses this to
    /// reconcile its registry against the daemon's view. Set by
    /// <see cref="AgentOrchestrator"/> at startup; when null, an empty array
    /// is sent (tests don't need to wire the callback).
    /// </summary>
    public Func<string[]>? GetLiveAgentIds { get; set; }

    public ServerConnection(DaemonConfig config, ILoggerFactory loggerFactory, ILogger<ServerConnection> logger) {
        _config = config;
        _logger = logger;

        _hub = new HubConnectionBuilder()
            .WithUrl(
                $"{config.ServerUrl.TrimEnd('/')}/hubs/sessions",
                options => {
                    options.AccessTokenProvider = async () => {
                        var tokens = await TokenStore.GetValidTokensAsync();

                        return tokens?.AccessToken;
                    };
                }
            )
            .WithAutomaticReconnect(new RetryPolicy())
            // Forward SignalR client framework logs (HubConnection, JsonHubProtocol,
            // …) to the daemon's logger factory. Without this, the HubConnectionBuilder
            // resolves a NullLoggerFactory internally and protocol-level errors
            // (e.g. "couldn't bind arguments for invocation 'LaunchAgent'" — exactly
            // what DEV-1665 was) silently disappear, leaving the daemon looking
            // healthy while it drops every invocation.
            .ConfigureLogging(b => b.Services.AddSingleton(loggerFactory))
            .AddJsonProtocol(options => {
                    options.PayloadSerializerOptions.TypeInfoResolverChain.Insert(0, CapacitorJsonContext.Default);
                    options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
                }
            )
            .Build();

        // Halve KeepAliveInterval (15s → 7s) so the WebSocket stays warm
        // through cloudflared and the server detects a dead transport sooner.
        // ServerTimeout stays at the 30s default to keep a safe 2× margin
        // against mixed-version rollouts where the server may still be on
        // the default 15s server-side KeepAliveInterval.
        _hub.KeepAliveInterval = TimeSpan.FromSeconds(7);

        _hub.On<LaunchAgentCommand>("LaunchAgent", cmd => SafeInvoke("LaunchAgent", () => OnLaunchAgent?.Invoke(cmd)));
        _hub.On<string>("StopAgent", agentId => SafeInvoke("StopAgent", () => OnStopAgent?.Invoke(agentId)));
        _hub.On<SendInputCommand>("SendInput", cmd => SafeInvoke("SendInput", () => OnSendInput?.Invoke(cmd)));
        _hub.On<string, string>("SendSpecialKey", (agentId, key) => SafeInvoke("SendSpecialKey", () => OnSendSpecialKey?.Invoke(agentId, key)));
        _hub.On<ResizeTerminalCommand>("ResizeTerminal", cmd => SafeInvoke("ResizeTerminal", () => OnResizeTerminal?.Invoke(cmd)));

        // Client-result invocations for per-phase eval dispatch.
        _hub.On<PrepareEvalCommand, PrepareResult>("PrepareEval",
            cmd => PrepareEvalHandler?.Invoke(cmd)
                ?? Task.FromResult(new PrepareResult(false, "no handler", null, 0, 0, 0, 0, 0)));

        _hub.On<RunQuestionCommand, QuestionResult>("RunQuestion",
            cmd => RunQuestionHandler?.Invoke(cmd)
                ?? Task.FromResult(new QuestionResult(false, null, "no handler", 0, 0)));

        _hub.On<FinalizeEvalCommand, FinalizeResult>("FinalizeEval",
            cmd => FinalizeEvalHandler?.Invoke(cmd)
                ?? Task.FromResult(new FinalizeResult(false, "no handler", null)));

        _hub.On<CancelEvalCommand>("CancelEval",
            cmd => CancelEvalHandler?.Invoke(cmd) ?? Task.CompletedTask);

        // Server probe used by the "Review this PR" UI to discover which
        // checkouts on this daemon match the PR's owner/repo. Returns an empty
        // array when the orchestrator hasn't wired a handler yet (e.g. during
        // startup) so the server treats this daemon as having no matches.
        _hub.On<FindRepoForRemoteRequest, string[]>("FindRepoForRemote",
            req => FindRepoForRemoteHandler?.Invoke(req) ?? Task.FromResult(Array.Empty<string>()));

        RegisterUiBroadcastSinks();

        _hub.Reconnected += OnReconnected;
        _hub.Closed      += OnClosed;

        _terminalSender = new TerminalOutputSender(
            (agentId, base64, ct) => _hub.SendAsync("SendTerminalOutput", new TerminalOutput(agentId, base64), ct),
            logger
        );
    }

    /// <summary>
    /// Registers no-op client handlers for the UI-only broadcasts a daemon can
    /// receive on its hub connection (AI-841). The server adds every
    /// authenticated connection — daemons included — to its <c>org-members</c>
    /// UI group in <c>CapacitorHub.OnConnectedAsync</c>, and only removes the
    /// daemon again inside <c>DaemonConnect</c>. In the window between the
    /// WebSocket connecting and <c>DaemonConnect</c> completing — which recurs on
    /// every (re)connect — the daemon receives these broadcasts with no matching
    /// handler, and SignalR's <c>JsonHubProtocol</c> logs a "Failed to find
    /// handler" warning plus an argument-bind failure for each one ("Invocation
    /// provides N argument(s) but target expects 0"). Registering sinks at the
    /// server's current arities, with <see cref="JsonElement"/> parameters that
    /// bind to any payload shape, silences the flood even against an
    /// already-deployed server. The permanent fix is server-side: never add a
    /// daemon connection to the UI group in the first place.
    /// </summary>
    void RegisterUiBroadcastSinks() {
        static Task Sink() => Task.CompletedTask;

        _hub.On("AgentInstancesChanged", Sink);
        _hub.On("DaemonsChanged",        Sink);
        _hub.On("WelcomeStateChanged",   Sink);

        _hub.On<JsonElement>("ActiveSessionAdded",   _ => Sink());
        _hub.On<JsonElement>("ActiveSessionChanged", _ => Sink());
        _hub.On<JsonElement>("ActiveSessionRemoved", _ => Sink());

        _hub.On<JsonElement, JsonElement>("LaunchFailed",        (_, _) => Sink());
        _hub.On<JsonElement, JsonElement>("PermissionResponded", (_, _) => Sink());

        _hub.On<JsonElement, JsonElement, JsonElement, JsonElement, JsonElement>(
            "PermissionRequested", (_, _, _, _, _) => Sink());
    }

    CancellationToken _ct;
    volatile bool     _disposed;
    Task?             _eventProcessorTask;

    readonly TerminalOutputSender _terminalSender;
    Task?                         _terminalSenderTask;

    /// <summary>
    /// <see cref="Stopwatch.GetTimestamp"/> taken each time the hub reaches a
    /// connected+registered state. Logged as connection uptime in
    /// <see cref="OnClosed"/> so the daemon log shows how long each connection
    /// survived — the cadence that distinguishes a steady transport from one
    /// flapping every few seconds (AI-840 diagnostics). Zero until the first
    /// successful connect.
    /// </summary>
    long _connectedTimestamp;

    /// <summary>
    /// Sentinel prefix the server (<c>DaemonRegistry.NameInUseErrorCode</c>)
    /// embeds in the <see cref="Microsoft.AspNetCore.SignalR.HubException"/>
    /// message when <c>DaemonConnect</c> is rejected because another live
    /// daemon already holds the <c>(owner, name)</c> slot. The daemon parses
    /// this prefix and exits with code 3 instead of force-reconnecting in a
    /// loop with the incumbent — see <see cref="OnNameInUse"/>.
    /// </summary>
    public const string NameInUseErrorCode = "DAEMON_NAME_IN_USE";

    /// <summary>
    /// Fires when the server rejected <c>DaemonConnect</c> with the
    /// <see cref="NameInUseErrorCode"/> prefix. <c>DaemonRunner</c>
    /// subscribes and signals host shutdown so the binary exits with
    /// code 3 rather than oscillating with the incumbent daemon.
    /// </summary>
    public event Action<string>? OnNameInUse;

    static bool IsNameInUse(Exception ex) =>
        ex is Microsoft.AspNetCore.SignalR.HubException he
        && he.Message.StartsWith(NameInUseErrorCode, StringComparison.Ordinal);

    public async Task ConnectAsync(CancellationToken ct) {
        _ct                 = ct;
        _eventProcessorTask = ProcessEventQueueAsync(ct);
        _terminalSenderTask = _terminalSender.RunAsync(ct);
        await ConnectWithRetryAsync(ct);
    }

    async Task ConnectWithRetryAsync(CancellationToken ct) {
        var delays  = new[] { 1, 2, 5, 10, 30 };
        var attempt = 0;

        while (!ct.IsCancellationRequested) {
            try {
                LogConnecting(_config.ServerUrl);
                await _hub.StartAsync(ct);
                await RegisterDaemon();
                _connectedTimestamp = Stopwatch.GetTimestamp();
                LogConnected(_config.Name);

                return;
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            } catch (Exception ex) when (IsNameInUse(ex)) {
                // AI-630: server explicitly rejected this daemon because
                // another live daemon owns the (owner, name) slot. Don't
                // retry — retrying would just thrash the incumbent.
                // RegisterDaemon already fired OnNameInUse before re-throwing
                // here; we just need to propagate so DaemonRunner exits
                // with code 3 instead of looping forever.
                throw;
            } catch (Exception ex) {
                var delay = delays[Math.Min(attempt, delays.Length - 1)];
                LogConnectionAttemptFailed(ex, attempt + 1, delay);
                await Task.Delay(TimeSpan.FromSeconds(delay), ct);
                attempt++;
            }
        }

        ct.ThrowIfCancellationRequested();
    }

    async Task OnClosed(Exception? ex) {
        if (_disposed || _ct.IsCancellationRequested) {
            return;
        }

        var uptimeSeconds = _connectedTimestamp == 0 ? 0 : Stopwatch.GetElapsedTime(_connectedTimestamp).TotalSeconds;
        LogConnectionClosed(ex, uptimeSeconds);

        try {
            await ConnectWithRetryAsync(_ct);
            OnReconnectedCallback?.Invoke();
        } catch (OperationCanceledException) when (_ct.IsCancellationRequested) {
            // Shutting down, ignore
        } catch (Exception ex2) when (IsNameInUse(ex2)) {
            // ConnectWithRetryAsync already fired OnNameInUse via
            // RegisterDaemon and propagated. The host's shutdown handler
            // will tear everything down; swallow here so OnClosed (an
            // unobserved Task) doesn't crash the process.
        }
    }

    async Task RegisterDaemon() {
        var platform  = $"{RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}";
        var repoPaths = await MergeRepoPathsAsync();
        var liveIds   = GetLiveAgentIds?.Invoke() ?? [];

        try {
            await _hub.InvokeAsync(
                "DaemonConnect",
                new DaemonConnect(
                    _config.Name, platform, repoPaths, _config.MaxConcurrentAgents, liveIds,
                    _config.InstanceId, _config.Version, _config.SupportedVendors
                ),
                cancellationToken: _ct
            );
        } catch (Exception ex) when (IsNameInUse(ex)) {
            // AI-630: server refused our (owner, name) slot because another
            // live daemon owns it. Surface to DaemonRunner before re-throwing
            // so the host can shut down cleanly; the heartbeat loop's
            // SafeReRegisterAsync filters this exception out so we don't
            // escalate to a pointless force-reconnect.
            LogNameInUse(_config.Name, ex.Message);
            OnNameInUse?.Invoke(ex.Message);

            throw;
        }
    }

    async Task<string[]> MergeRepoPathsAsync() {
        var persisted = await RepoPathStore.GetSortedPathsAsync();

        if (_config.AllowedRepoPaths.Length == 0)
            return persisted;

        // Union: persisted paths first (sorted by last_used desc), then config-only paths
        var comparer = RepoPathStore.PathComparison == StringComparison.Ordinal
            ? StringComparer.Ordinal
            : StringComparer.OrdinalIgnoreCase;
        var seen = new HashSet<string>(persisted, comparer);
        var merged = new List<string>(persisted);
        merged.AddRange(_config.AllowedRepoPaths.Select(p => p.TrimEnd('/', '*')).Where(seen.Add));

        return [..merged];
    }

    async Task OnReconnected(string? connectionId) {
        LogReconnected();
        await RegisterDaemon();
        _connectedTimestamp = Stopwatch.GetTimestamp();
        OnReconnectedCallback?.Invoke();
    }

    public event Action? OnReconnectedCallback;

    /// <summary>
    /// Round-trip liveness probe (AI-566). Calls <c>DaemonPing</c> on the server
    /// and returns whether this connection is still the registered daemon for
    /// its <c>(owner, name)</c> slot. <c>false</c> means the slot was displaced
    /// — usually by an auto-reconnect Register from a different conn id —
    /// and the daemon should re-register so the orchestrator's view is
    /// repaired. <c>virtual</c> so unit tests can override without spinning
    /// up a real SignalR client.
    /// </summary>
    public Task<bool> PingAsync(CancellationToken ct)
        => _hub.InvokeAsync<bool>("DaemonPing", cancellationToken: ct);

    /// <summary>
    /// Re-runs <c>DaemonConnect</c> on the existing hub connection. Used by
    /// the heartbeat loop when the server reports it doesn't recognise this
    /// connection as a daemon (slot displaced or never registered).
    /// </summary>
    public Task ReRegisterAsync() => RegisterDaemon();

    /// <summary>
    /// Stops the underlying hub. <see cref="OnClosed"/> fires and calls
    /// <see cref="ConnectWithRetryAsync"/>, which establishes a fresh
    /// transport and a new server-side conn id, then re-registers via
    /// <see cref="RegisterDaemon"/>. Used when the heartbeat ping times out
    /// or throws — the WebSocket is hung and only a fresh connection
    /// recovers it. StopAsync is capped at 5 s so a wedged transport
    /// can't stall the heartbeat loop indefinitely (Qodo AI-642).
    /// </summary>
    public async Task ForceReconnectAsync() {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        try {
            await _hub.StopAsync(cts.Token);
        } catch (OperationCanceledException) when (!_ct.IsCancellationRequested) {
            // StopAsync didn't return in 5 s — transport is wedged. OnClosed
            // may still fire eventually, but we don't want to block the
            // heartbeat loop on it. The next tick will retry.
            _logger.LogWarning("ForceReconnectAsync: StopAsync exceeded 5 s — abandoning wait");
        }
    }

    public virtual async Task UpdateRepoPathsAsync() {
        try {
            var repoPaths = await MergeRepoPathsAsync();
            await _hub.InvokeAsync("DaemonUpdateRepoPaths", repoPaths, cancellationToken: _ct);
        } catch (Exception ex) {
            LogRepoPathUpdateFailed(ex);
        }
    }

    // Outgoing messages to server
    public virtual Task AgentRegisteredAsync(string agentId, string? prompt, string? model, string? effort, string? repoPath)
        => _hub.InvokeAsync("AgentRegistered", new AgentRegistered(agentId, prompt, model, effort, repoPath), cancellationToken: _ct);

    public virtual Task AgentStatusChangedAsync(string agentId, string status, string? sessionId)
        => _hub.InvokeAsync("AgentStatusChanged", new AgentStatusChanged(agentId, status, sessionId), cancellationToken: _ct);

    public virtual Task AgentUnregisteredAsync(string agentId)
        => _hub.InvokeAsync("AgentUnregistered", new AgentUnregistered(agentId), cancellationToken: _ct);

    public virtual Task LaunchFailedAsync(string agentId, string reason)
        => _hub.InvokeAsync("LaunchFailed", new LaunchFailed(agentId, reason), cancellationToken: _ct);

    /// <summary>
    /// Tells the server to end the AgentSession for a daemon-hosted agent. Used when
    /// the daemon stops or observes a hosted claude exiting, since claude isn't
    /// guaranteed to fire its own <c>session-end</c> hook on SIGTERM. The server-side
    /// handler is idempotent — if SessionEnded was already written (e.g. claude did
    /// fire session-end first), this call is a no-op.
    ///
    /// The result carries the resolved <c>SessionId</c> (the daemon only knows
    /// agentId; the server resolves the link) plus a <c>GenerateWhatsDone</c> flag.
    /// When the flag is true and SessionId is non-null, the daemon should spawn
    /// <c>kcap generate-whats-done {sessionId}</c> locally — matching the
    /// behaviour of the CLI session-end handler for the local-claude case.
    /// </summary>
    public virtual Task<EndAgentSessionResult> EndAgentSessionAsync(string agentId, string reason)
        => _hub.InvokeAsync<EndAgentSessionResult>("EndAgentSession", agentId, reason, cancellationToken: _ct);

    /// <summary>
    /// Forwards a hosted-agent permission request to the server's <c>RequestPermission</c>
    /// hub method and returns the user's decision. Runs over the persistent SignalR
    /// connection so the long-poll isn't subject to the Cloudflare HTTP-request timeout
    /// that severs the equivalent <c>/hooks/permission-request</c> route at ~120s.
    /// The provided <paramref name="ct"/> typically tracks daemon shutdown — HttpListener
    /// in the bridge doesn't surface a per-request "client disconnected" signal, so a
    /// Claude process exiting mid-wait won't cancel this call. Switching the bridge to
    /// Kestrel + <c>HttpContext.RequestAborted</c> would give us per-request cancellation.
    ///
    /// The bridge knows the vendor ("claude" or "codex") locally to pick the right hook
    /// response shape in <c>LocalPermissionBridge.BuildHookResponseJson</c>, but the
    /// server's permission flow is vendor-agnostic so it is NOT forwarded over the wire.
    /// Sending it would add a 5th positional argument that <c>JsonHubProtocol.BindArguments</c>
    /// strict-count-matches against the server signature (default values are not honoured
    /// by the binder), breaking every hosted-agent permission prompt against any server
    /// build whose hub method doesn't declare a matching 5th parameter.
    /// </summary>
    public virtual Task<PermissionDecision> RequestPermissionAsync(
            string            sessionId,
            string?           toolName,
            JsonElement?      toolInput,
            JsonElement?      suggestions,
            CancellationToken ct = default
        ) =>
        _hub.InvokeAsync<PermissionDecision>(
            "RequestPermission",
            sessionId,
            toolName,
            toolInput,
            suggestions,
            ct
        );

    /// <summary>
    /// Queues a base64 PTY chunk for the hosted-agent terminal mirror. AI-842:
    /// chunks are drained by <see cref="TerminalOutputSender"/>'s single ordered
    /// loop instead of being fired at <c>SendAsync</c> fire-and-forget, so they
    /// reach the server in PTY order and a chunk sent while the transport is down
    /// is held and retried rather than silently lost mid-escape-sequence.
    /// </summary>
    public virtual Task SendTerminalOutputAsync(string agentId, string base64Data) {
        _terminalSender.Enqueue(agentId, base64Data);

        return Task.CompletedTask;
    }

    // ── Eval progress events (DEV-1440) ────────────────────────────────────

    public Task EvalStartedAsync(string evalRunId, string sessionId, string judgeModel, int totalQuestions)
        => _hub.SendAsync("EvalStarted", new EvalStarted(evalRunId, sessionId, judgeModel, totalQuestions), cancellationToken: _ct);

    public Task EvalQuestionStartedAsync(string evalRunId, string sessionId, int index, int total, string category, string questionId)
        => _hub.SendAsync("EvalQuestionStarted", new EvalQuestionStarted(evalRunId, sessionId, index, total, category, questionId), cancellationToken: _ct);

    public Task EvalQuestionCompletedAsync(string evalRunId, string sessionId, int index, int total, string category, string questionId, int score, string verdict)
        => _hub.SendAsync("EvalQuestionCompleted", new EvalQuestionCompleted(evalRunId, sessionId, index, total, category, questionId, score, verdict), cancellationToken: _ct);

    public Task EvalQuestionFailedAsync(string evalRunId, string sessionId, int index, int total, string category, string questionId, string reason)
        => _hub.SendAsync("EvalQuestionFailed", new EvalQuestionFailed(evalRunId, sessionId, index, total, category, questionId, reason), cancellationToken: _ct);

    public Task EvalFinishedAsync(string evalRunId, string sessionId, int overallScore, string summary)
        => _hub.SendAsync("EvalFinished", new EvalFinished(evalRunId, sessionId, overallScore, summary), cancellationToken: _ct);

    public Task EvalFailedAsync(string evalRunId, string sessionId, string reason)
        => _hub.SendAsync("EvalFailed", new EvalFailed(evalRunId, sessionId, reason), cancellationToken: _ct);

    // ── Retrospective progress events (DEV-1470) ───────────────────────────

    public Task EvalRetrospectiveStartedAsync(string sessionId, string evalRunId)
        => _hub.SendAsync("EvalRetrospectiveStarted", new EvalRetrospectiveStarted(sessionId, evalRunId), cancellationToken: _ct);

    public Task EvalRetrospectiveCompletedAsync(string sessionId, string evalRunId)
        => _hub.SendAsync("EvalRetrospectiveCompleted", new EvalRetrospectiveCompleted(sessionId, evalRunId), cancellationToken: _ct);

    public Task EvalRetrospectiveFailedAsync(string sessionId, string evalRunId, string reason)
        => _hub.SendAsync("EvalRetrospectiveFailed", new EvalRetrospectiveFailed(sessionId, evalRunId, reason), cancellationToken: _ct);

    public virtual Task AppendAgentRunEventAsync(string agentId, object evt) {
        _eventChannel.Writer.TryWrite(new PendingEvent(agentId, evt));

        return Task.CompletedTask;
    }

    readonly Channel<PendingEvent> _eventChannel = Channel.CreateBounded<PendingEvent>(
        new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest }
    );

    HttpClient? _httpClient;

    async Task ProcessEventQueueAsync(CancellationToken ct) {
        try {
            await foreach (var evt in _eventChannel.Reader.ReadAllAsync(ct)) {
                string payload;

                try {
                    var eventType = evt.Event.GetType().Name;
                    var data      = JsonSerializer.SerializeToNode(evt.Event, evt.Event.GetType(), CapacitorJsonContext.Default)!.AsObject();

                    var payloadObj = new JsonObject {
                        ["event_type"] = eventType,
                        ["data"]       = data
                    };
                    payload = payloadObj.ToJsonString();
                } catch (Exception ex) {
                    LogEventSerializationFailed(ex, evt.Event.GetType().Name, evt.AgentId);

                    continue;
                }

                var url        = $"{_config.ServerUrl.TrimEnd('/')}/api/agent-runs/{evt.AgentId}/events";
                var retryDelay = TimeSpan.FromSeconds(1);

                while (!ct.IsCancellationRequested) {
                    try {
                        _httpClient ??= new();
                        var tokens = await TokenStore.GetValidTokensAsync();

                        if (tokens?.AccessToken is not null) {
                            _httpClient.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);
                        }

                        var response = await _httpClient.PostAsync(url, new StringContent(payload, Encoding.UTF8, "application/json"), ct);
                        response.EnsureSuccessStatusCode();

                        break;
                    } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                        return;
                    } catch (Exception ex) {
                        LogEventPostFailed(ex, retryDelay.TotalSeconds);

                        try {
                            await Task.Delay(retryDelay, ct);
                        } catch (OperationCanceledException) {
                            return;
                        }

#pragma warning disable IDE0059
                        retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 30));
#pragma warning restore IDE0059
                    }
                }
            }
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            // Graceful shutdown — channel read cancelled
        }
    }

    record PendingEvent(string AgentId, object Event);

    public async ValueTask DisposeAsync() {
        _disposed = true;
        _eventChannel.Writer.TryComplete();
        _terminalSender.Complete();

        if (_eventProcessorTask is not null) {
            await _eventProcessorTask;
        }

        if (_terminalSenderTask is not null) {
            await _terminalSenderTask;
        }

        _httpClient?.Dispose();
        await _hub.DisposeAsync();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Daemon name '{Name}' is already in use by another live daemon on this account. Server rejected DaemonConnect: {Reason}")]
    partial void LogNameInUse(string name, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connecting to {Url}...")]
    partial void LogConnecting(string url);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected and registered as '{Name}'")]
    partial void LogConnected(string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connection attempt {Attempt} failed, retrying in {Delay}s")]
    partial void LogConnectionAttemptFailed(Exception ex, int attempt, int delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "SignalR connection closed after {UptimeSeconds:F1}s uptime, will reconnect")]
    partial void LogConnectionClosed(Exception? ex, double uptimeSeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconnected to server, re-registering daemon")]
    partial void LogReconnected();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to post agent run event, retrying in {Delay}s")]
    partial void LogEventPostFailed(Exception ex, double delay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Failed to serialize {EventType} for agent {AgentId}, dropping event")]
    partial void LogEventSerializationFailed(Exception ex, string eventType, string agentId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Failed to update repo paths on server")]
    partial void LogRepoPathUpdateFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Hub method '{Method}' handler threw — invocation dropped")]
    partial void LogHandlerThrew(Exception ex, string method);

    /// <summary>
    /// Wraps each typed <c>On(...)</c> handler so an exception inside a handler
    /// is logged with the hub method name instead of bubbling up into SignalR's
    /// generic dispatch error path. Pairs with the <c>ConfigureLogging</c> wiring
    /// above, which surfaces the framework's own binding/parsing errors. Together
    /// they make sure no class of "daemon silently dropped a server invocation"
    /// is invisible in the logs.
    /// </summary>
    async Task SafeInvoke(string method, Func<Task?> handler) {
        try {
            var task = handler();

            if (task is not null) await task;
        } catch (Exception ex) {
            LogHandlerThrew(ex, method);
        }
    }

    class RetryPolicy : IRetryPolicy {
        static readonly TimeSpan[] Delays = [
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30)
        ];

        public TimeSpan? NextRetryDelay(RetryContext retryContext) {
            var index = Math.Min(retryContext.PreviousRetryCount, Delays.Length - 1);

            return Delays[index]; // Keeps retrying at 30s intervals after initial backoff
        }
    }
}
