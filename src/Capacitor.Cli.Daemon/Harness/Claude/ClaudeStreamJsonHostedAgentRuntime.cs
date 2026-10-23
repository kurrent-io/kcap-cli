using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Harness.Claude;

/// <summary>
/// Claude Code driven over its stream-json protocol: every user message, the first included, is a
/// JSON line on stdin, and a permission prompt or question arrives as a <c>can_use_tool</c> control
/// request answered on stdin. The hooks and the transcript watcher still record the session, so
/// nothing on stdout is forwarded.
/// </summary>
internal sealed partial class ClaudeStreamJsonHostedAgentRuntime : IHostedAgentRuntime {
    const string DeniedMessage = "The user denied this tool use.";

    readonly IJsonLineProcess                                            _process;
    readonly Func<JsonObject, CancellationToken, Task<PermissionDecision>> _decide;
    readonly Func<string, string?>                                       _transcriptPathOf;
    readonly string                                                      _agentId;
    readonly string                                                      _cwd;
    readonly ILogger                                                     _logger;
    readonly TimeProvider                                                _time;
    readonly CancellationTokenSource                                     _lifetime = new();
    readonly ConcurrentDictionary<string, CancellationTokenSource>       _asks     = new();
    readonly Task                                                        _readLoop;

    volatile string? _sessionId;
    int              _inputClosed;
    int              _disposed;

    /// <param name="decide">Answers one permission request, given in the shape the hook posts to the
    /// bridge.</param>
    /// <param name="transcriptPathOf">The transcript a session id is recorded in, for the judge.</param>
    internal ClaudeStreamJsonHostedAgentRuntime(
            IJsonLineProcess                                              process,
            Func<JsonObject, CancellationToken, Task<PermissionDecision>> decide,
            Func<string, string?>                                         transcriptPathOf,
            string                                                        agentId,
            string                                                        cwd,
            ILogger                                                       logger,
            TimeProvider                                                  time) {
        _process          = process;
        _decide           = decide;
        _transcriptPathOf = transcriptPathOf;
        _agentId          = agentId;
        _cwd              = cwd;
        _logger           = logger;
        _time             = time;
        _readLoop         = ReadLoopAsync();
    }

    public string Vendor                 => "claude";
    public int    Pid                    => _process.Pid;
    public bool   HasExited              => _process.HasExited;
    public int?   ExitCode               => _process.ExitCode;
    public bool   EmitsTerminalOutput    => false;
    public bool   AsksPermissionsOnStdin => true;

    /// <summary>Null until the file exists: a reader linked to a path not yet written reports the transcript missing.</summary>
    internal (string SessionId, string Path)? KnownTranscript =>
        _sessionId is { } id && _transcriptPathOf(id) is { } path && File.Exists(path) ? (id, path) : null;

    /// <summary>Holds the orchestrator's read loop open until the child's stdout ends.</summary>
    public async IAsyncEnumerable<byte[]> ReadOutputAsync([EnumeratorCancellation] CancellationToken ct = default) {
        await _readLoop.WaitAsync(ct).ConfigureAwait(false);
        yield break;
    }

    public Task SendUserInputAsync(string text) {
        if (Volatile.Read(ref _inputClosed) != 0) throw new InputNotAdmittedException("Claude: the hosted session has ended.");

        return WriteAsync(UserMessage(text));
    }

    /// <summary>What the Agent SDK sends before its first message; the launch is verified this way.</summary>
    internal Task SendInitializeAsync() => WriteAsync(ControlRequest("initialize"));

    public async Task SendSpecialKeyAsync(string key) {
        if (!string.Equals(key, "escape", StringComparison.OrdinalIgnoreCase)) return;

        try {
            await WriteAsync(ControlRequest("interrupt")).ConfigureAwait(false);
        } catch (Exception ex) {
            LogInterruptFailed(ex, _agentId);
        }
    }

    public Task SendRawInputAsync(byte[] data) =>
        throw new NotSupportedException("Local-attach raw input is a PTY-only surface; this Claude runtime has no terminal.");

    public void Resize(ushort cols, ushort rows) { }

    /// <summary>Interrupts the running turn and closes stdin, which ends the session once the turn
    /// stops.</summary>
    public async Task RequestGracefulStopAsync() {
        await SendSpecialKeyAsync("escape").ConfigureAwait(false);
        Volatile.Write(ref _inputClosed, 1);
        await _process.CloseInputAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }

    public Task WaitForExitAsync(TimeSpan? timeout = null) => _process.WaitForExitAsync(timeout);

    public async Task TerminateAsync(TimeSpan? timeout = null) {
        Volatile.Write(ref _inputClosed, 1);
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _process.TerminateAsync(timeout).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        Volatile.Write(ref _inputClosed, 1);
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _process.DisposeAsync().ConfigureAwait(false);

        try {
            await _readLoop.WaitAsync(TimeSpan.FromSeconds(2), _time).ConfigureAwait(false);
        } catch {
        }

        _lifetime.Dispose();
    }

    async Task ReadLoopAsync() {
        await Task.Yield();

        try {
            await foreach (var line in _process.ReadLinesAsync(_lifetime.Token).ConfigureAwait(false)) {
                JsonElement message;

                try {
                    using var doc = JsonDocument.Parse(line);
                    message = doc.RootElement.Clone();
                } catch (JsonException) {
                    continue;
                }

                if (message.Str("session_id") is { Length: > 0 } sessionId) _sessionId = sessionId;

                switch (message.Str("type")) {
                    case "control_request":
                        OnControlRequest(message);
                        break;
                    case "control_cancel_request":
                        if (message.Str("request_id") is { } cancelled && _asks.TryRemove(cancelled, out var ask)) CancelQuietly(ask);
                        break;
                }
            }
        } catch (OperationCanceledException) {
        } catch (Exception ex) {
            LogReadLoopFailed(ex, _agentId);
        } finally {
            Volatile.Write(ref _inputClosed, 1);
            foreach (var ask in _asks.Values) CancelQuietly(ask);
        }
    }

    void OnControlRequest(JsonElement message) {
        if (message.Str("request_id") is not { } requestId || message.Obj("request") is not { } request) return;

        if (request.Str("subtype") == "can_use_tool") {
            _ = AnswerAsync(requestId, request);
            return;
        }

        _ = WriteQuietlyAsync(new JsonObject {
            ["type"] = "control_response",
            ["response"] = new JsonObject {
                ["subtype"]    = "error",
                ["request_id"] = requestId,
                ["error"]      = $"unsupported control request: {request.Str("subtype")}"
            }
        });
    }

    async Task AnswerAsync(string requestId, JsonElement request) {
        using var ask = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _asks[requestId] = ask;

        try {
            PermissionDecision decision;

            if (_sessionId is not { } sessionId) {
                decision = PermissionSettlements.DenyDecision;
            } else {
                try {
                    decision = await _decide(BridgeRequest(sessionId, request), ask.Token).ConfigureAwait(false);
                } catch (Exception ex) when (ex is not OperationCanceledException) {
                    LogDecisionFailed(ex, _agentId);
                    decision = PermissionSettlements.DenyDecision;
                }
            }

            if (ask.IsCancellationRequested) return;

            await WriteAsync(PermissionResponse(requestId, decision, request.Prop("input"))).ConfigureAwait(false);
        } catch (OperationCanceledException) {
        } catch (Exception ex) {
            LogAnswerFailed(ex, _agentId);
        } finally {
            _asks.TryRemove(new KeyValuePair<string, CancellationTokenSource>(requestId, ask));
        }
    }

    /// <summary>The request in the shape Claude's permission hook posts to the bridge, so one decision
    /// path serves both.</summary>
    JsonObject BridgeRequest(string sessionId, JsonElement request) {
        var payload = new JsonObject {
            ["session_id"]             = sessionId,
            ["tool_name"]              = request.Str("tool_name") ?? "Unknown",
            ["tool_input"]             = NodeOf(request.Prop("input")),
            ["permission_suggestions"] = NodeOf(request.Prop("permission_suggestions")),
            ["agent_id"]               = _agentId,
            ["cwd"]                    = _cwd
        };

        if (request.Str("tool_use_id") is { } toolUseId) payload["tool_use_id"] = toolUseId;
        if (_transcriptPathOf(sessionId) is { } transcript) payload["transcript_path"] = transcript;
        if (request.Str("agent_id") is { Length: > 0 } subagentId) payload["subagent_id"] = subagentId;

        return payload;
    }

    internal static JsonObject PermissionResponse(string requestId, PermissionDecision decision, JsonElement? input) {
        var result = decision.Behavior == PermissionSettlements.Allow
            ? new JsonObject {
                ["behavior"]     = "allow",
                ["updatedInput"] = NodeOf(decision.UpdatedInput ?? input) ?? new JsonObject()
            }
            : new JsonObject {
                ["behavior"] = "deny",
                ["message"]  = DeniedMessage
            };

        if (decision.Behavior == PermissionSettlements.Allow && decision.ApplyPermissions is { ValueKind: JsonValueKind.Array } rules)
            result["updatedPermissions"] = NodeOf(rules);

        return new JsonObject {
            ["type"] = "control_response",
            ["response"] = new JsonObject {
                ["subtype"]    = "success",
                ["request_id"] = requestId,
                ["response"]   = result
            }
        };
    }

    internal static JsonObject UserMessage(string text) => new() {
        ["type"]               = "user",
        ["message"]            = new JsonObject { ["role"] = "user", ["content"] = text },
        ["parent_tool_use_id"] = null,
        ["session_id"]         = ""
    };

    internal static JsonObject ControlRequest(string subtype) => new() {
        ["type"]       = "control_request",
        ["request_id"] = Guid.NewGuid().ToString("N"),
        ["request"]    = new JsonObject { ["subtype"] = subtype }
    };

    /// <summary>The answer that owns <paramref name="ask"/> may finish and dispose it first.</summary>
    static void CancelQuietly(CancellationTokenSource ask) {
        try {
            ask.Cancel();
        } catch (ObjectDisposedException) {
        }
    }

    static JsonNode? NodeOf(JsonElement? element) =>
        element is { } e && !e.IsNull ? JsonNode.Parse(e.GetRawText()) : null;

    Task WriteAsync(JsonNode message) => _process.WriteLineAsync(message.ToJsonString(), _lifetime.Token);

    async Task WriteQuietlyAsync(JsonNode message) {
        try {
            await WriteAsync(message).ConfigureAwait(false);
        } catch (Exception ex) {
            LogAnswerFailed(ex, _agentId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Claude stream-json: reading the agent's output failed (agentId={AgentId})")]
    partial void LogReadLoopFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Claude stream-json: deciding a permission request failed; denying it (agentId={AgentId})")]
    partial void LogDecisionFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Claude stream-json: answering a control request failed (agentId={AgentId})")]
    partial void LogAnswerFailed(Exception ex, string agentId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Claude stream-json: the interrupt was not written (agentId={AgentId})")]
    partial void LogInterruptFailed(Exception ex, string agentId);
}
