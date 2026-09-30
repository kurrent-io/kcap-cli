using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Capacitor.Cli.Continuation;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Telemetry;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.Cli.Commands;

/// <summary>Continues a session whose agent is gone. Kept out of every tier-gated server, because
/// continuing must work on every plan, and out of kcap-sessions, which reviewers auto-approve whole.</summary>
sealed class McpHandoffServer(ConfigRoot config, ProfileContext profiles, TokenStore tokens, ICapacitorHttpClient http,
        TelemetryStartup startup, TimeProvider time) {
    internal const string ToolName = "continue_session";

    internal const string NoCurrentSessionMessage =
        "Error: no current session. continue_session must run inside the harness session that takes over (CLAUDE_CODE_SESSION_ID, KCAP_SESSION_ID or CODEX_THREAD_ID).";

    public async Task<int> RunAsync() {
        var baseUrl = profiles.Resolution.ServerUrl!;
        var current = WorkContextIds.CanonicalSessionId(HarnessRequesterContext.Resolve().SessionId);

        var tools = BuildToolsList();

        // Best-effort, and recorded even when the read throws: a stale token on disk must never
        // block the server from starting.
        var loggedIn = false;
        try { loggedIn = await tokens.LoadForProfileAsync(profiles.Name) is not null; } catch { }

        // MCP servers are long-lived and denylisted under the top-level "mcp" command; the
        // reportable pseudo-command "mcp-server" is what lets per-tool-call events leave.
        var telemetry = CliTelemetry.Start(startup with { Command = "mcp-server" }, config, time);
        telemetry.AddSharedProperty("logged_in", loggedIn);

        await using var mcp = new McpTelemetry(telemetry);

        var urlOk = HttpClientExtensions.IsAcceptableUrl(baseUrl);

        // Created on demand so a session that never calls a tool pays no network cost; nullable so
        // a transient creation failure leaves it null and the next call retries. The stdio loop
        // handles one request at a time, so no locking.
        HttpClient? client = null;

        async Task<string> DispatchToolCallAsync(JsonNode callId, JsonObject callRequest) {
            if (!urlOk)
                return BuildToolResult(callId, HttpClientExtensions.SchemeMissingHint, isError: true);

            try {
                client ??= await http.ForSessionAsync();
                return await HandleToolCallAsync(callId, callRequest, client, baseUrl, current);
            } catch (Exception ex) {
                // The detail goes to stderr, not the client, which could leak local paths.
                await Console.Error.WriteLineAsync($"kcap mcp handoff: unexpected error handling tools/call: {ex}");
                return BuildToolResult(callId, "Error: internal error handling the request.", isError: true);
            }
        }

        async Task<string> TimedDispatchToolCallAsync(JsonNode callId, JsonObject callRequest) {
            var start = time.GetTimestamp();
            var tool  = McpTelemetry.SafeToolName(callRequest);
            var ok    = false;

            try {
                var response = await DispatchToolCallAsync(callId, callRequest);
                ok = McpTelemetry.ResponseOk(response);
                return response;
            } finally {
                mcp.ToolCalled("kcap-handoff", tool, ok, CommandTiming.ElapsedMs(start, time));
            }
        }

        await using var stdin  = Console.OpenStandardInput();
        await using var stdout = Console.OpenStandardOutput();
        using var       reader = new StreamReader(stdin, Encoding.UTF8);
        await using var writer = new StreamWriter(stdout, new UTF8Encoding(false));
        writer.AutoFlush = true;

        try {
            while (await reader.ReadLineAsync() is { } line) {
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonObject? request;

                try {
                    request = JsonNode.Parse(line)?.AsObject();
                } catch {
                    continue;
                }

                if (request is null) continue;

                var id     = request["id"];
                var method = DecodeMethod(request);

                // Notifications have no id — don't send a response
                if (id is null) continue;

                var response = method switch {
                    null         => BuildErrorResponse(id, -32600, "Invalid request: method must be a string"),
                    "initialize" => BuildInitializeResponse(id, request),
                    "tools/list" => BuildToolsListResponse(id, tools),
                    "tools/call" => await TimedDispatchToolCallAsync(id, request),
                    _            => McpProtocol.TryHandleStandardMethod(method, id)
                                    ?? BuildErrorResponse(id, -32601, $"Method not found: {method}")
                };

                await writer.WriteLineAsync(response);
            }
        } finally {
            if (client is not null) {
                try { client.Dispose(); } catch {
                    /* swallow — best-effort cleanup */
                }
            }
        }

        return 0;
    }

    internal const string ServerInstructions =
        "Use continue_session when the user asks you to continue, resume or pick up another session's work — " +
        "typically one whose agent was killed. It attaches this session to that session's work items and " +
        "unfinished plans. If it refuses because the session may still be running, ask the user before " +
        "retrying with force: true. Then check the working tree before resuming: a task left in progress may be half-done.";

    static string BuildInitializeResponse(JsonNode id, JsonObject request) =>
        ToResponse<McpInitResult>(
            id,
            new(McpProtocol.NegotiateVersion(request), new(new()), new("kcap-handoff", "1.0.0"), ServerInstructions),
            McpJsonContext.Default.McpInitResult
        );

    static string BuildToolsListResponse(JsonNode id, McpTool[] tools) =>
        ToResponse(id, new McpToolsResult(tools), McpJsonContext.Default.McpToolsResult);

    internal async Task<string> HandleToolCallAsync(JsonNode id, JsonObject request, HttpClient client, string baseUrl, string? currentSessionId) {
        var paramsNode = request["params"]?.AsObject();
        var toolName   = paramsNode?["name"]?.GetValue<string>();
        var arguments  = paramsNode?["arguments"]?.AsObject();

        if (toolName is null) return BuildErrorResponse(id, -32602, "Missing params.name");
        if (toolName != ToolName) return BuildToolResult(id, $"Error: Unknown tool: {toolName}", isError: true);

        try {
            if (currentSessionId is null) return BuildToolResult(id, NoCurrentSessionMessage, isError: true);

            var previous = McpToolArguments.RequireString(arguments, "session_id");
            var force    = OptionalBool(arguments, "force");

            var result = await new SessionTakeover(AgentSessions.OnThisMachine(config), time)
                .RunAsync(client, baseUrl, previous, currentSessionId, force);

            return result switch {
                TakeoverResult.Completed c    => BuildToolResult(id, c.Outcome.ToJsonString(), isError: c.Unsuccessful),
                TakeoverResult.Refused r      => BuildToolResult(id, $"Error: {r.Reason}", isError: true),
                TakeoverResult.Failed f       => BuildToolResult(id, $"Error: {f.Reason}", isError: true),
                TakeoverResult.Unauthorized   => BuildToolResult(id, await AuthRejectionNotice.ForPersistentUnauthorizedAsync(tokens, profiles.Name, baseUrl, time), isError: true),
                _                             => throw new InvalidOperationException(result.GetType().Name),
            };
        } catch (ArgumentException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        } catch (HttpRequestException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        }
    }

    static bool OptionalBool(JsonObject? args, string name) =>
        args?[name] switch {
            null                                       => false,
            JsonValue v when v.TryGetValue(out bool b) => b,
            _                                          => throw new ArgumentException($"'{name}' must be a boolean."),
        };

    /// <summary>Null for a present but wrong-shaped <c>method</c> instead of throwing — a malformed
    /// request must yield an invalid-request response, never terminate the stdio loop.</summary>
    internal static string? DecodeMethod(JsonObject request) {
        try {
            return request["method"]?.GetValue<string>();
        } catch {
            return null;
        }
    }

    static string BuildToolResult(JsonNode id, string text, bool isError = false) =>
        ToResponse<McpToolCallResult>(id, new([new("text", text)], isError ? true : null), McpJsonContext.Default.McpToolCallResult);

    static string BuildErrorResponse(JsonNode id, int code, string message) {
        var envelope = new JsonObject {
            ["jsonrpc"] = "2.0",
            ["id"]      = id.DeepClone(),
            ["error"]   = JsonSerializer.SerializeToNode(new McpError(code, message), McpJsonContext.Default.McpError)
        };

        return envelope.ToJsonString();
    }

    static string ToResponse<T>(JsonNode id, T result, JsonTypeInfo<T> typeInfo) {
        var envelope = new JsonObject {
            ["jsonrpc"] = "2.0",
            ["id"]      = id.DeepClone(),
            ["result"]  = JsonSerializer.SerializeToNode(result, typeInfo)
        };

        return envelope.ToJsonString();
    }

    internal static McpTool[] BuildToolsList() => [
        new(ToolName,
            "Continue another session's work: attach this session to its work items and unfinished plans, so "
          + "the app shows them here. Refuses while that session may still be running — a live agent process "
          + "on this machine, or a session active in the last hour that did not run here; ask the user before "
          + "retrying with force. Work items are skipped, not failed, on a plan without them. Returns what was "
          + "attached and skipped, and which plan is now current.",
            new("object", new() {
                ["session_id"] = new("string", "The session whose work this session takes over."),
                ["force"]      = new("boolean", "Take over even when that session looks live. Only after the user confirms.")
            }, ["session_id"]), McpToolAnnotations.Additive)
    ];
}
