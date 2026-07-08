// src/Capacitor.Cli.Daemon/Services/AcpHostedAgentRuntime.cs
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Acp;
using Capacitor.Cli.Daemon.Acp;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// <see cref="IHostedAgentRuntime"/> that drives an ACP (Agent Client Protocol) session over
/// <see cref="AcpConnection"/> for Cursor (AI-684 Task 9). Owns the <c>initialize</c> →
/// <c>session/new</c> → <c>session/prompt</c> handshake and reduces inbound <c>session/update</c>
/// notifications to <see cref="AcpSessionUpdate"/> DTOs, surfaced via <see cref="Updates"/> for
/// AI-685's mapper to turn into canonical events. AI-684 scope stops there — no canonical events, no
/// permission bridge (<c>OnServerRequest</c> stays unset, so the connection's default-decline
/// posture answers any inbound server request with a method-not-found error; AI-686 wires the real
/// bridge). Local-attach (raw byte input) and terminal output are PTY-only surfaces the ACP runtime
/// does not support until AI-687 adds a terminal capability.
/// </summary>
internal sealed class AcpHostedAgentRuntime : IHostedAgentRuntime {
    static readonly object[] NoMcpServers = [];

    readonly AcpConnection _connection;
    readonly IAcpProcess   _process;
    readonly ILogger       _logger;
    readonly AcpInteractionBridge? _interactionBridge;
    readonly CancellationTokenSource _cts = new();
    readonly Channel<AcpSessionUpdate> _updates = Channel.CreateUnbounded<AcpSessionUpdate>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });

    // Tracks every session/prompt turn fired as untracked-by-the-caller background work (the
    // initial prompt from StartAsync, plus every SendUserInputAsync) so DisposeAsync can wait for
    // them to wind down and so a fault never becomes an unobserved task exception. A
    // ConcurrentDictionary-as-set (value is unused) rather than a List so concurrent
    // SendUserInputAsync calls can register/remove their own task without a lock.
    readonly ConcurrentDictionary<Task, byte> _backgroundPromptTasks = new();

    Task    _connectionRunTask = Task.CompletedTask;
    string? _sessionId;
    int     _disposed;

    /// <summary>
    /// <paramref name="requestInteraction"/> is optional (AI-686) — when null, matches AI-684's
    /// original behavior exactly: <see cref="AcpConnection.OnServerRequest"/> stays unset, and any
    /// <c>session/request_permission</c>/<c>elicitation/create</c> the agent sends gets the
    /// connection's default JSON-RPC "Method not found" response. When provided, it is forwarded
    /// into a new <see cref="AcpInteractionBridge"/> and wired as <see cref="AcpConnection.OnServerRequest"/>.
    ///
    /// <b>Qodo daemon-review Q2:</b> this wiring no longer closes over this runtime's
    /// <see cref="_sessionId"/> — <see cref="AcpInteractionBridge.HandleAsync"/> now sources the ACP
    /// session id solely from the inbound request's OWN params. The prior shape passed
    /// <c>_sessionId ?? ""</c>, which was correct ONLY because a permission/elicitation request can
    /// normally arrive no earlier than a <c>session/prompt</c> turn, by which point
    /// <see cref="StartAsync"/>'s <c>session/new</c> has already resolved <see cref="_sessionId"/> —
    /// but <see cref="AcpConnection"/>'s read loop is started (via <see cref="RunConnectionLoopAsync"/>)
    /// BEFORE that handshake completes, so a server request arriving out of turn (a buggy or
    /// malicious agent) would have forwarded an <see cref="AcpInteractionRequest"/> with
    /// <c>AcpSessionId == ""</c>, silently breaking server-side correlation instead of failing loud
    /// or safe. Trusting the request's own params removes this whole class of bug and this runtime
    /// no longer needs to expose <see cref="_sessionId"/> to the bridge at all.
    /// </summary>
    public AcpHostedAgentRuntime(
            AcpConnection                                                                  connection,
            IAcpProcess                                                                    process,
            ILogger                                                                        logger,
            string                                                                         agentId = "",
            Func<AcpInteractionRequest, CancellationToken, Task<AcpInteractionDecision>>?   requestInteraction = null
        ) {
        _connection = connection;
        _process    = process;
        _logger     = logger;

        _connection.OnNotification += HandleNotification;

        if (requestInteraction is not null) {
            _interactionBridge = new AcpInteractionBridge(requestInteraction, agentId, logger);
            _connection.OnServerRequest = (request, ct) => _interactionBridge.HandleAsync(request, ct);
        }
    }

    public string Vendor              => "cursor";
    public int    Pid                 => _process.Pid;
    public bool   HasExited           => _process.HasExited;
    public int?   ExitCode            => _process.ExitCode;
    public bool   EmitsTerminalOutput => false;

    /// <summary>The ACP <c>sessionId</c> once <see cref="StartAsync"/>'s <c>session/new</c> has resolved; null before that.</summary>
    public string? SessionId => _sessionId;

    /// <summary>
    /// Reduced <c>session/update</c> notifications, in arrival order. Unbounded so a mapper that
    /// attaches slightly late (or is momentarily busy) never misses an update — the alternative
    /// (a plain event) would drop anything raised before a subscriber attaches.
    /// </summary>
    public ChannelReader<AcpSessionUpdate> Updates => _updates.Reader;

    /// <summary>
    /// Performs the ACP handshake: starts the connection's read loop, then
    /// <c>initialize</c> → <c>session/new</c> (with the absolute <paramref name="cwd"/>) →
    /// (AI-688 gap 1) an optional model-selection step — resolves <paramref name="requestedModel"/>
    /// against <c>session/new</c>'s <c>availableModels</c> and, if it matches, sends
    /// <c>session/set_config_option</c> and awaits the response BEFORE the first turn fires (see
    /// <see cref="TrySelectModelAsync"/>). If <paramref name="initialPrompt"/> is non-empty, fires
    /// the initial <c>session/prompt</c> as tracked background work (see
    /// <see cref="FireAndTrackPromptAsync"/>) and returns as soon as the session is established — it
    /// does NOT await that prompt turn to completion (AI-684 Fix E). Not part of
    /// <see cref="IHostedAgentRuntime"/> — called directly by the Task 10 factory (and by tests)
    /// once the connection/process are constructed. A failed handshake surfaces a clear exception
    /// (never hangs): the read loop is started before any request is sent, and every request goes
    /// through <see cref="AcpConnection.RequestAsync"/>, which itself never hangs past
    /// <paramref name="ct"/> cancellation. Model selection is NEVER part of that "failed handshake"
    /// exception path — an unresolved or rejected model just falls back to Cursor's own default
    /// (see <see cref="TrySelectModelAsync"/>'s remarks).
    /// </summary>
    public async Task StartAsync(string cwd, string? initialPrompt, CancellationToken ct, string? requestedModel = null) {
        _connectionRunTask = RunConnectionLoopAsync(_cts.Token);

        JsonElement sessionNewResult;

        try {
            var initializeParams = JsonSerializer.SerializeToElement(
                new InitializeParams(
                    ProtocolVersion: 1,
                    ClientCapabilities: new ClientCapabilities(
                        Fs: new FsCapabilities(ReadTextFile: false, WriteTextFile: false),
                        Terminal: false)),
                CapacitorJsonContext.Default.InitializeParams);

            await _connection.RequestAsync("initialize", initializeParams, ct).ConfigureAwait(false);

            var sessionNewParams = JsonSerializer.SerializeToElement(
                new SessionNewParams(Cwd: cwd, McpServers: NoMcpServers),
                CapacitorJsonContext.Default.SessionNewParams);

            sessionNewResult = await _connection.RequestAsync("session/new", sessionNewParams, ct).ConfigureAwait(false);

            if (!sessionNewResult.TryGetProperty("sessionId", out var sessionIdElement) || sessionIdElement.GetString() is not { Length: > 0 } sessionId)
                throw new InvalidOperationException("ACP session/new response did not contain a sessionId.");

            _sessionId = sessionId;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            throw new InvalidOperationException("ACP handshake (initialize/session-new) failed.", ex);
        }

        // AI-688 gap 1: select the requested model (if any) BEFORE the first prompt fires. Awaited,
        // but never fatal — see TrySelectModelAsync's remarks.
        await TrySelectModelAsync(sessionNewResult, requestedModel, ct).ConfigureAwait(false);

        // The session is established (initialize + session/new both completed) — the caller
        // (orchestrator) can now treat this agent as live. Fire the initial turn without awaiting
        // it: a real ACP turn can run arbitrarily long, and blocking StartAsync on it would delay
        // agent registration/stoppability for the whole turn (AI-684 Fix E). Completion is
        // observed via the Updates channel, not this method's return.
        if (!string.IsNullOrEmpty(initialPrompt))
            FireAndTrackPromptAsync(initialPrompt);
    }

    /// <summary>
    /// AI-688 gap 1: resolves <paramref name="requestedModel"/> (already merged by the caller from
    /// <c>ctx.Model</c>/<c>DaemonConfig.CursorModel</c> — see
    /// <c>AcpHostedAgentRuntimeFactory.ResolveRequestedModel</c>) against
    /// <paramref name="sessionNewResult"/>'s <c>models.availableModels</c> via
    /// <see cref="AcpModelResolver.Resolve"/> and, if it resolves, sends
    /// <c>session/set_config_option</c> and AWAITS the response before returning — the model must
    /// be set before <see cref="SendPromptAsync"/> fires the first turn. Never throws: no requested
    /// model, an unparsable/missing <c>models</c> object, no match, or a JSON-RPC error response are
    /// all logged (where relevant) and treated as "use Cursor's default model" — per the AI-688
    /// probe findings (<c>docs/ai-688-cursor-prototype-findings.md</c>), model selection is a
    /// nice-to-have, never a launch precondition.
    /// </summary>
    async Task TrySelectModelAsync(JsonElement sessionNewResult, string? requestedModel, CancellationToken ct) {
        if (string.IsNullOrWhiteSpace(requestedModel))
            return;

        AvailableModelDto[]? availableModels = null;

        if (sessionNewResult.TryGetProperty("models", out var modelsElement)) {
            try {
                availableModels = JsonSerializer
                    .Deserialize(modelsElement.GetRawText(), CapacitorJsonContext.Default.SessionModelsInfo)
                    ?.AvailableModels;
            } catch (JsonException ex) {
                _logger.LogDebug(ex, "ACP: failed to parse session/new 'models' object; skipping model selection.");
                return;
            }
        }

        var resolvedModelId = AcpModelResolver.Resolve(requestedModel, availableModels);

        if (resolvedModelId is null) {
            _logger.LogWarning(
                "ACP: requested model '{RequestedModel}' was not found in session/new's availableModels; continuing with Cursor's default model.",
                requestedModel);
            return;
        }

        var setConfigOptionParams = JsonSerializer.SerializeToElement(
            new SetConfigOptionParams(SessionId: _sessionId!, ConfigId: "model", Value: resolvedModelId),
            CapacitorJsonContext.Default.SetConfigOptionParams);

        try {
            await _connection.RequestAsync("session/set_config_option", setConfigOptionParams, ct).ConfigureAwait(false);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            // Covers AcpRpcException (a well-formed JSON-RPC error response) and any other
            // non-cancellation failure — per the probe findings this is explicitly non-fatal.
            _logger.LogWarning(
                ex,
                "ACP: session/set_config_option failed for model '{ResolvedModelId}'; continuing with Cursor's default model.",
                resolvedModelId);
        }
    }

    /// <summary>
    /// Fires a <c>session/prompt</c> turn as tracked, untracked-by-the-caller background work:
    /// registers the task in <see cref="_backgroundPromptTasks"/> (removing itself on completion)
    /// so <see cref="DisposeAsync"/> can wait for in-flight turns to wind down, and swallows/logs
    /// any fault so it never becomes an unobserved task exception. Used by both
    /// <see cref="StartAsync"/> (initial prompt) and <see cref="SendUserInputAsync"/> (follow-up
    /// turns) — neither caller may block on turn completion; that's what <see cref="Updates"/> is
    /// for.
    /// </summary>
    void FireAndTrackPromptAsync(string text) {
        Task? task = null;

        task = SendPromptAsync(text, _cts.Token).ContinueWith(t => {
            _backgroundPromptTasks.TryRemove(task!, out _);

            if (t.IsFaulted)
                _logger.LogDebug(t.Exception, "ACP: background session/prompt turn faulted.");
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        _backgroundPromptTasks[task] = 0;
    }

    async Task RunConnectionLoopAsync(CancellationToken ct) {
        try {
            await _connection.RunAsync(ct).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            // normal shutdown
        } catch (Exception ex) {
            _logger.LogDebug(ex, "ACP connection read loop ended unexpectedly.");
        } finally {
            _updates.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Never yields a byte — ACP stdout is protocol traffic, never terminal output (no terminal
    /// capability until AI-687). Crucially, this must NOT complete until the process exits or
    /// <paramref name="ct"/> cancels (AI-684 Fix B/E): <see cref="AgentOrchestrator.ReadAgentOutputAsync"/>
    /// treats the enumerable ending as "the agent's output stream ended" and finalizes the agent —
    /// for a PTY that's a real signal (the CLI exited), but the old implementation here
    /// (<c>yield break</c> on the first call) made a LIVE ACP agent look like it had already
    /// finished, so the orchestrator immediately finalized it as failed. Staying open for the
    /// process's whole lifetime means the orchestrator's read loop parks harmlessly (yielding
    /// nothing) instead of ending prematurely; <see cref="IHostedAgentRuntime.EmitsTerminalOutput"/>
    /// tells the orchestrator not to use this stream for the Starting→Running/startup-failure
    /// signals it uses for PTY runtimes.
    /// </summary>
    public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default) {
        var exitTask = _process.WaitForExitAsync(); // completes on process exit (AcpChildProcess swallows faults)
        var ctTcs    = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var reg = ct.Register(() => ctTcs.TrySetResult());

        await Task.WhenAny(exitTask, ctTcs.Task).ConfigureAwait(false);

        yield break;
    }

    /// <summary>
    /// Sends a follow-up <c>session/prompt</c> for hosted-UI text input (server <c>SendInput</c>).
    /// Returns as soon as the request is WRITTEN to the wire — it does NOT await the turn's
    /// <c>stopReason</c> response (AI-684 Fix E): a real turn can run arbitrarily long, and the
    /// pre-fix behavior (awaiting the full round trip) blocked this call — and therefore the
    /// orchestrator's <c>HandleSendInput</c> — for the whole turn. Turn completion is observed via
    /// <see cref="Updates"/>, not this method's return.
    /// </summary>
    public Task SendUserInputAsync(string text) {
        RequireSessionId();
        FireAndTrackPromptAsync(text);

        return Task.CompletedTask;
    }

    async Task SendPromptAsync(string text, CancellationToken ct) {
        var promptParams = JsonSerializer.SerializeToElement(
            new SessionPromptParams(
                SessionId: _sessionId!,
                Prompt: [new PromptContentBlock(Type: "text", Text: text)]),
            CapacitorJsonContext.Default.SessionPromptParams);

        await _connection.RequestAsync("session/prompt", promptParams, ct).ConfigureAwait(false);
    }

    public Task SendSpecialKeyAsync(string key) {
        // ACP has no special-key channel — best-effort no-op.
        _logger.LogDebug("ACP runtime ignoring SendSpecialKeyAsync({Key}) — no special-key surface in ACP.", key);
        return Task.CompletedTask;
    }

    public Task SendRawInputAsync(byte[] data) =>
        throw new NotSupportedException("Local-attach raw input is a PTY-only surface; the ACP runtime has no equivalent channel.");

    public void Resize(ushort cols, ushort rows) {
        // No terminal capability until AI-687 — no-op.
    }

    public async Task RequestGracefulStopAsync() {
        if (_sessionId is not { Length: > 0 } sessionId) {
            _logger.LogDebug("ACP runtime RequestGracefulStopAsync called before a session was established; nothing to cancel.");
            return;
        }

        var cancelParams = JsonSerializer.SerializeToElement(
            new SessionCancelParams(SessionId: sessionId),
            CapacitorJsonContext.Default.SessionCancelParams);

        await _connection.NotifyAsync("session/cancel", cancelParams).ConfigureAwait(false);
    }

    public Task WaitForExitAsync(TimeSpan? timeout = null) => _process.WaitForExitAsync(timeout);
    public Task TerminateAsync(TimeSpan?   timeout = null) => _process.TerminateAsync(timeout);

    void HandleNotification(AcpNotification notification) {
        if (notification.Method != "session/update")
            return;

        if (notification.Params is not { } @params || !@params.TryGetProperty("update", out var updateElement)) {
            _logger.LogDebug("ACP: session/update notification missing 'update' object; skipping.");
            return;
        }

        var reduced = Reduce(updateElement.Clone());
        if (!_updates.Writer.TryWrite(reduced))
            _logger.LogDebug("ACP: dropped a session/update — updates channel already completed.");
    }

    static AcpSessionUpdate Reduce(JsonElement update) {
        var kindText = update.TryGetProperty("sessionUpdate", out var kindEl) ? kindEl.GetString() : null;

        return kindText switch {
            "agent_message_chunk" => new AcpSessionUpdate(
                AcpUpdateKind.AgentMessageChunk,
                Text: ExtractContentText(update),
                Raw: update),

            "agent_thought_chunk" => new AcpSessionUpdate(
                AcpUpdateKind.AgentThoughtChunk,
                Text: ExtractContentText(update),
                Raw: update),

            "tool_call" => new AcpSessionUpdate(
                AcpUpdateKind.ToolCall,
                ToolCallId: GetStringOrNull(update, "toolCallId"),
                ToolTitle: GetStringOrNull(update, "title"),
                ToolKind: GetStringOrNull(update, "kind"),
                ToolStatus: GetStringOrNull(update, "status"),
                ToolInputJson: GetRawTextOrNull(update, "rawInput"),
                Raw: update),

            "tool_call_update" => new AcpSessionUpdate(
                AcpUpdateKind.ToolCallUpdate,
                ToolCallId: GetStringOrNull(update, "toolCallId"),
                ToolStatus: GetStringOrNull(update, "status"),
                ToolResultText: ExtractToolResultText(update),
                ToolIsError: GetStringOrNull(update, "status") == "failed",
                Raw: update),

            "plan" => new AcpSessionUpdate(AcpUpdateKind.Plan, Raw: update),

            "available_commands_update" => new AcpSessionUpdate(AcpUpdateKind.AvailableCommands, Raw: update),

            _ => new AcpSessionUpdate(AcpUpdateKind.Unknown, Raw: update),
        };
    }

    static string? ExtractContentText(JsonElement update) =>
        update.TryGetProperty("content", out var content) && content.TryGetProperty("text", out var textEl)
            ? textEl.GetString()
            : null;

    static string? GetStringOrNull(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) ? value.GetString() : null;

    /// <summary>
    /// AI-688 Option B task 1 (§2.2 footnote 2): verbatim JSON text of <paramref name="propertyName"/>
    /// (e.g. a <c>tool_call</c>'s <c>rawInput</c> object) if present and not JSON <c>null</c>, else
    /// <see langword="null"/> — used to populate <see cref="AcpSessionUpdate.ToolInputJson"/> without
    /// re-serializing/reshaping the tool's input args (the mapper on the server side parses this raw
    /// text itself; see <c>AcpSessionMapper.BuildToolCall</c>).
    /// </summary>
    static string? GetRawTextOrNull(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined
            ? value.GetRawText()
            : null;

    /// <summary>
    /// AI-688 Option B task 1 (§2.2 footnote 2, defensive/spec-derived — the tool_call_update result
    /// shape is NOT probe-confirmed, see docs/acp-probe-findings.md): extracts a tool_call_update's
    /// RESULT text for <see cref="AcpSessionUpdate.ToolResultText"/>, mechanically and regardless of
    /// <c>status</c> (the terminal-status gate lives in <c>AcpEventTranslator</c>, not here). Prefers
    /// the ACP-spec <c>content</c> array's text-block shape (<c>ToolCallContent</c>:
    /// <c>{type:"content", content:{type:"text", text:"..."}}</c>) — concatenating every such block
    /// found (newline-joined); non-text content variants (<c>diff</c>/<c>terminal</c>) are not
    /// extracted here, degrading to "no result text from this block" rather than throwing. Falls back
    /// to the verbatim <c>rawOutput</c> JSON text when no text content block is present. Returns
    /// <see langword="null"/> when neither is present/extractable, so
    /// <c>AcpEventTranslator.Translate</c> never emits an empty <c>ToolResultReceived</c>.
    /// </summary>
    static string? ExtractToolResultText(JsonElement update) {
        if (update.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.Array) {
            List<string>? texts = null;

            foreach (var block in contentEl.EnumerateArray()) {
                if (block.ValueKind != JsonValueKind.Object) continue;
                if (!block.TryGetProperty("type", out var blockType) || blockType.GetString() != "content") continue;
                if (!block.TryGetProperty("content", out var inner) || inner.ValueKind != JsonValueKind.Object) continue;
                if (!inner.TryGetProperty("type", out var innerType) || innerType.GetString() != "text") continue;
                if (!inner.TryGetProperty("text", out var textEl) || textEl.GetString() is not { } text) continue;

                (texts ??= []).Add(text);
            }

            if (texts is { Count: > 0 })
                return string.Join("\n", texts);
        }

        return GetRawTextOrNull(update, "rawOutput");
    }

    void RequireSessionId() {
        if (_sessionId is not { Length: > 0 })
            throw new InvalidOperationException("AcpHostedAgentRuntime.SendUserInputAsync called before StartAsync established a session.");
    }

    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _connection.OnNotification -= HandleNotification;

        await _cts.CancelAsync().ConfigureAwait(false);
        _updates.Writer.TryComplete();

        // Every background session/prompt turn (StartAsync's initial prompt, and each
        // SendUserInputAsync) is keyed off _cts.Token via AcpConnection.RequestAsync's own
        // cancellation registration, so cancelling _cts above already unblocks each one — this is
        // just a bounded wait for them to actually finish removing themselves from the tracking set
        // (FireAndTrackPromptAsync's continuation already swallows/logs any fault, so nothing here
        // can throw or need re-observing).
        try {
            await Task.WhenAll(_backgroundPromptTasks.Keys).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        } catch {
            // Best-effort — a stuck background turn must never hang dispose.
        }

        try {
            await _connectionRunTask.ConfigureAwait(false);
        } catch (OperationCanceledException) {
            // expected shutdown path
        }

        _cts.Dispose();

        await _connection.DisposeAsync().ConfigureAwait(false);
        await _process.DisposeAsync().ConfigureAwait(false);
    }
}
