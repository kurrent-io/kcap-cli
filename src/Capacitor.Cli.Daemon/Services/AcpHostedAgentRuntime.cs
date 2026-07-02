// src/Capacitor.Cli.Daemon/Services/AcpHostedAgentRuntime.cs
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
    readonly CancellationTokenSource _cts = new();
    readonly Channel<AcpSessionUpdate> _updates = Channel.CreateUnbounded<AcpSessionUpdate>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });

    Task    _connectionRunTask = Task.CompletedTask;
    string? _sessionId;
    int     _disposed;

    public AcpHostedAgentRuntime(AcpConnection connection, IAcpProcess process, ILogger logger) {
        _connection = connection;
        _process    = process;
        _logger     = logger;

        _connection.OnNotification += HandleNotification;
    }

    public string Vendor    => "cursor";
    public int    Pid       => _process.Pid;
    public bool   HasExited => _process.HasExited;
    public int?   ExitCode  => _process.ExitCode;

    /// <summary>
    /// Reduced <c>session/update</c> notifications, in arrival order. Unbounded so a mapper that
    /// attaches slightly late (or is momentarily busy) never misses an update — the alternative
    /// (a plain event) would drop anything raised before a subscriber attaches.
    /// </summary>
    public ChannelReader<AcpSessionUpdate> Updates => _updates.Reader;

    /// <summary>
    /// Performs the ACP handshake: starts the connection's read loop, then
    /// <c>initialize</c> → <c>session/new</c> (with the absolute <paramref name="cwd"/>) →, if
    /// <paramref name="initialPrompt"/> is non-empty, <c>session/prompt</c>. Not part of
    /// <see cref="IHostedAgentRuntime"/> — called directly by the Task 10 factory (and by tests)
    /// once the connection/process are constructed. A failed handshake surfaces a clear exception
    /// (never hangs): the read loop is started before any request is sent, and every request goes
    /// through <see cref="AcpConnection.RequestAsync"/>, which itself never hangs past
    /// <paramref name="ct"/> cancellation.
    /// </summary>
    public async Task StartAsync(string cwd, string? initialPrompt, CancellationToken ct) {
        _connectionRunTask = RunConnectionLoopAsync(_cts.Token);

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

            var sessionNewResult = await _connection.RequestAsync("session/new", sessionNewParams, ct).ConfigureAwait(false);

            if (!sessionNewResult.TryGetProperty("sessionId", out var sessionIdElement) || sessionIdElement.GetString() is not { Length: > 0 } sessionId)
                throw new InvalidOperationException("ACP session/new response did not contain a sessionId.");

            _sessionId = sessionId;

            if (!string.IsNullOrEmpty(initialPrompt))
                await SendPromptAsync(initialPrompt, ct).ConfigureAwait(false);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            throw new InvalidOperationException("ACP handshake (initialize/session-new/session-prompt) failed.", ex);
        }
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

    public IAsyncEnumerable<byte[]> ReadOutputAsync(CancellationToken ct = default) => EmptyOutputAsync();

    static async IAsyncEnumerable<byte[]> EmptyOutputAsync() {
        // No terminal until AI-687 — ACP stdout is protocol traffic, never terminal output.
        await Task.CompletedTask;
        yield break;
    }

    public Task SendUserInputAsync(string text) {
        RequireSessionId();
        return SendPromptAsync(text, CancellationToken.None);
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
