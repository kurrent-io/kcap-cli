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
    /// <c>initialize</c> → <c>session/new</c> (with the absolute <paramref name="cwd"/>). If
    /// <paramref name="initialPrompt"/> is non-empty, fires the initial <c>session/prompt</c> as
    /// tracked background work (see <see cref="FireAndTrackPromptAsync"/>) and returns as soon as
    /// the session is established — it does NOT await that prompt turn to completion (AI-684 Fix
    /// E). Not part of <see cref="IHostedAgentRuntime"/> — called directly by the Task 10 factory
    /// (and by tests) once the connection/process are constructed. A failed handshake surfaces a
    /// clear exception (never hangs): the read loop is started before any request is sent, and
    /// every request goes through <see cref="AcpConnection.RequestAsync"/>, which itself never
    /// hangs past <paramref name="ct"/> cancellation.
    /// </summary>
    public async Task StartAsync(string cwd, string? initialPrompt, CancellationToken ct) {
        _connectionRunTask = RunConnectionLoopAsync(_cts.Token);

        try {
            // Advertise NO client fs/terminal: cursor-agent does file/shell ops itself and never asks
            // the client to serve them (rationale: docs/ai-687-fs-terminal-capability-decision-design.md).
            // Any unadvertised request is declined -32601 by AcpConnection, never falsely acknowledged.
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

            var sessionNewResult = await _connection.RequestAsync("session/new", sessionNewParams, ct).ConfigureAwait(false);

            if (!sessionNewResult.TryGetProperty("sessionId", out var sessionIdElement) || sessionIdElement.GetString() is not { Length: > 0 } sessionId)
                throw new InvalidOperationException("ACP session/new response did not contain a sessionId.");

            _sessionId = sessionId;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            throw new InvalidOperationException("ACP handshake (initialize/session-new) failed.", ex);
        }

        // The session is established (initialize + session/new both completed) — the caller
        // (orchestrator) can now treat this agent as live. Fire the initial turn without awaiting
        // it: a real ACP turn can run arbitrarily long, and blocking StartAsync on it would delay
        // agent registration/stoppability for the whole turn (AI-684 Fix E). Completion is
        // observed via the Updates channel, not this method's return.
        if (!string.IsNullOrEmpty(initialPrompt))
            FireAndTrackPromptAsync(initialPrompt);
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
                Raw: update),

            "tool_call_update" => new AcpSessionUpdate(
                AcpUpdateKind.ToolCallUpdate,
                ToolCallId: GetStringOrNull(update, "toolCallId"),
                ToolStatus: GetStringOrNull(update, "status"),
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
