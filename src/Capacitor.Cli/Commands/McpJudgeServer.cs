using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Telemetry;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Eval.Evidence;

using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

sealed class McpJudgeServer(ConfigRoot config, ProfileContext profiles, TokenStore tokens, ICapacitorHttpClient http,
        TelemetryStartup startup, TimeProvider time) {
    /// <summary>
    /// Run as a session-scoped MCP server. All tool calls must use <paramref name="expectedSessionId"/>;
    /// with <paramref name="runPath"/>, evidence-route tool calls read that owner-only run file instead.
    /// </summary>
    public async Task<int> RunAsync(string expectedSessionId, string? runPath = null) {
        if (runPath is not null && !RunFileBelongsTo(runPath, expectedSessionId)) {
            Console.Error.WriteLine($"kcap mcp judge: the run file does not belong to session {expectedSessionId}");
            return 1;
        }

        var baseUrl = profiles.Resolution.ServerUrl!;

        // Validate the shape locally, then defer client construction to the first tools/call —
        // the shape every sibling MCP server already uses.
        var urlOk = HttpClientExtensions.IsAcceptableUrl(baseUrl);
        using var sessionClient = new OnDemand<HttpClient>(() => http.ForSessionAsync());
        using var evidence = new OnDemand<McpEvidenceJudgeTools>(async () =>
            McpEvidenceJudgeTools.Open(runPath!, await sessionClient.GetAsync(), baseUrl, time));

        // Best-effort, and recorded even when the read throws: a stale token on disk must never
        // block the server from starting, and an absent property is a different value in a funnel
        // from a false one — "could not tell" belongs with "not logged in", not with a gap.
        var loggedIn = false;
        try { loggedIn = await tokens.LoadForProfileAsync(profiles.Name) is not null; } catch { }

        // MCP servers are long-lived and denylisted under the top-level "mcp" command
        // (CommandEvents.Denylisted) — a second facade under the reportable pseudo-command
        // "mcp-server" is what lets per-tool-call events leave at all.
        var telemetry = CliTelemetry.Start(startup with { Command = "mcp-server" }, config, time);
        telemetry.AddSharedProperty("logged_in", loggedIn);

        await using var mcp = new McpTelemetry(telemetry);

        var tools = ToolsFor(evidence: runPath is not null);

        await using var stdin  = Console.OpenStandardInput();
        await using var stdout = Console.OpenStandardOutput();
        using var       reader = new StreamReader(stdin, Encoding.UTF8);
        await using var writer = new StreamWriter(stdout, new UTF8Encoding(false));
        writer.AutoFlush = true;

        // In arrival order: an evidence run's ledger, budgets and page handles form one sequence.
        await McpStdioLoop.RunAsync(reader, writer, tools, TimedDispatchToolCallAsync, (id, method, request) => method switch {
            "initialize" => BuildInitializeResponse(id, request),
            "tools/list" => BuildToolsListResponse(id, tools),
            _            => McpProtocol.TryHandleStandardMethod(method, id)
                            ?? BuildErrorResponse(id, -32601, $"Method not found: {method}")
        }, "judge", McpStdioLoop.InArrivalOrder);

        return 0;

        // Report an unusable server_url as a JSON-RPC tool error rather than dying: a server is
        // expected to keep serving, and the caller can see the reason.
        async Task<string> DispatchToolCallAsync(JsonNode callId, JsonObject callRequest) {
            if (!urlOk) return BuildToolResult(callId, HttpClientExtensions.SchemeMissingHint, isError: true);

            var client = await sessionClient.GetAsync();

            if (runPath is null) return await HandleToolCallAsync(callId, callRequest, client, baseUrl, expectedSessionId);

            McpEvidenceJudgeTools judgeTools;
            try {
                judgeTools = await evidence.GetAsync();
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException) {
                return BuildToolResult(callId, $"Error: the evidence run could not be opened: {e.Message}", isError: true);
            }

            return await DispatchEvidenceCallAsync(callId, callRequest, judgeTools);
        }

        // Records which MCP tools agents actually reach for. Never touches the response path:
        // the result (or the exception) is returned exactly as DispatchToolCallAsync produced it.
        async Task<string> TimedDispatchToolCallAsync(JsonNode callId, JsonObject callRequest) {
            var start = time.GetTimestamp();
            var tool  = McpTelemetry.SafeToolName(callRequest);
            var ok    = false;

            try {
                var response = await DispatchToolCallAsync(callId, callRequest);
                ok = McpTelemetry.ResponseOk(response);
                return response;
            } finally {
                mcp.ToolCalled("kcap-judge", tool, ok, CommandTiming.ElapsedMs(start, time));
            }
        }
    }

    internal static async Task<string> DispatchEvidenceCallAsync(JsonNode callId, JsonObject callRequest, McpEvidenceJudgeTools evidence) {
        var parameters = callRequest["params"] as JsonObject;
        if (StringOf(parameters?["name"]) is not { } name) return BuildErrorResponse(callId, -32602, "Missing params.name");
        var (text, isError) = await evidence.CallAsync(name, parameters!["arguments"], CancellationToken.None);
        return BuildToolResult(callId, text, isError);
    }

    static string? StringOf(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Test hook: execute a tool-call handler and return the JSON-RPC envelope.</summary>
    internal static async Task<string> HandleToolCallForTests(
            string     toolName,
            JsonObject arguments,
            HttpClient client,
            string     baseUrl,
            string     expectedSessionId
        ) {
        JsonNode id = 1;

        var request = new JsonObject {
            ["jsonrpc"] = "2.0",
            ["id"]      = id.DeepClone(),
            ["method"]  = "tools/call",
            ["params"] = new JsonObject {
                ["name"]      = toolName,
                ["arguments"] = arguments.DeepClone()
            }
        };

        return await HandleToolCallAsync(id, request, client, baseUrl, expectedSessionId);
    }

    static async Task<string> HandleToolCallAsync(
            JsonNode   id,
            JsonObject request,
            HttpClient client,
            string     baseUrl,
            string     expectedSessionId
        ) {
        var paramsNode = request["params"] as JsonObject;
        var toolName   = StringOf(paramsNode?["name"]);
        var arguments  = paramsNode?["arguments"] as JsonObject;

        if (toolName is null) {
            return BuildErrorResponse(id, -32602, "Missing params.name");
        }

        try {
            var apiRoot   = baseUrl.TrimEnd('/');
            var sessionId = arguments?["session_id"]?.GetValue<string>();

            if (sessionId is null) {
                return BuildToolResult(id, "Error: missing required argument: session_id", isError: true);
            }

            if (sessionId != expectedSessionId) {
                return BuildToolResult(
                    id,
                    $"Error: session_id '{sessionId}' does not match this judge's bound session",
                    isError: true
                );
            }

            var encoded = Uri.EscapeDataString(sessionId);
            var offset  = OptionalInt(arguments, "offset") ?? 0;
            var limit   = toolName == "get_session_errors"
                ? OptionalInt(arguments, "limit", max: JudgeErrorDigest.MaxLimit, min: 1) ?? JudgeErrorDigest.DefaultLimit
                : 0;

            using var httpResponse = toolName switch {
                "get_session_recap"   => await client.GetAsync($"{apiRoot}/api/sessions/{encoded}/recap?chain=true"),
                "get_session_errors"  => await client.GetAsync($"{apiRoot}/api/sessions/{encoded}/errors?chain=true"),
                "get_transcript"      => await client.GetAsync(BuildTranscriptUrl(apiRoot, sessionId, arguments)),
                "get_session_summary" => await client.GetAsync($"{apiRoot}/api/sessions/{encoded}/eval-summary"),
                "search_session"      => await client.PostAsync(
                    $"{apiRoot}/api/sessions/{encoded}/search",
                    JsonContent.Create(BuildSearchBody(arguments), McpJsonContext.Default.SessionSearchQuery)
                ),
                "get_tool_result"     => await client.GetAsync(BuildToolResultUrl(apiRoot, encoded, arguments)),
                _                     => throw new ArgumentException($"Unknown tool: {toolName}")
            };

            var body = await httpResponse.Content.ReadAsStringAsync();

            if (!httpResponse.IsSuccessStatusCode) {
                return BuildToolResult(id, JudgeResultCap.Cap($"Error: HTTP {(int)httpResponse.StatusCode} — {body}", "The error body was cut."), isError: true);
            }

            return BuildToolResult(id, Shape(toolName, body, arguments, sessionId, offset, limit));
        } catch (ArgumentException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        } catch (HttpRequestException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        }
    }

    static string Shape(string toolName, string body, JsonObject? arguments, string sessionId, int offset, int limit) {
        switch (toolName) {
            case "get_tool_result":                          return JudgeResultCap.PageOutput(body, offset);
            case "get_session_recap" or "get_session_summary": return JudgeResultCap.Page(body, offset);
        }

        string shaped;
        try {
            shaped = toolName switch {
                "get_session_errors" => JudgeErrorDigest.Render(body, sessionId, offset, limit),
                "search_session" => JudgeSearchExcerpts.Render(body, GetRequiredString(arguments, "query")),
                _                => body
            };
        } catch (JsonException) {
            shaped = body;
        }

        // The digest and the search page fit by construction; the cap is a backstop for a body neither could parse.
        return JudgeResultCap.Cap(shaped, toolName switch {
            "get_transcript"     => "Pass a smaller take, or skip past what you have read.",
            "get_session_errors" => "Page with offset.",
            _                    => "Pass a smaller limit or a narrower query."
        });
    }

    static int? OptionalInt(JsonObject? arguments, string name, int max = int.MaxValue, int min = 0) {
        if (arguments?[name] is not { } node) return null;

        int value;
        try {
            value = node.GetValue<int>();
        } catch (Exception e) when (e is InvalidOperationException or FormatException) {
            throw new ArgumentException($"{name} must be an integer");
        }

        return value < min || value > max
            ? throw new ArgumentException(max == int.MaxValue ? $"{name} must be at least {min}" : $"{name} must be between {min} and {max}")
            : value;
    }

    static string BuildInitializeResponse(JsonNode id, JsonObject request) =>
        ToResponse<McpInitResult>(
            id,
            new(McpProtocol.NegotiateVersion(request), new(new()), new("kcap-judge", "1.0.0")),
            McpJsonContext.Default.McpInitResult
        );

    static string BuildToolsListResponse(JsonNode id, McpTool[] tools) =>
        ToResponse(id, new McpToolsResult(tools), McpJsonContext.Default.McpToolsResult);

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

    internal static McpTool[] ToolsFor(bool evidence) => evidence ? McpEvidenceJudgeTools.ToolsList() : BuildToolsList();

    static bool RunFileBelongsTo(string runPath, string sessionId) {
        static string Normalize(string id) => id.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        try {
            return Normalize(EvidenceRunFile.Read(runPath).RootSessionId) == Normalize(sessionId);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException) {
            return false;
        }
    }

    internal static McpTool[] BuildToolsList() => [
        new(
            "get_session_recap",
            "Get a short narrative recap of the session's user inputs, assistant replies, and tool invocations. "
          + "Start here — it's the cheapest way to orient yourself before pulling specific transcript slices. A long recap comes back a page at a time.",
            new(
                "object",
                new() {
                    ["session_id"] = new("string", "Session ID to recap (must match the judge's bound session)"),
                    ["offset"]     = new("integer", "Character offset to continue a long result from (the offset its last page names)")
                },
                ["session_id"]
            ),
            McpToolAnnotations.Read
        ),
        new(
            "get_session_errors",
            "List the failed tool calls recorded in the session and its continuations (tool errors, non-zero exits, exceptions). "
          + "repeated_operations groups failures that share a tool and operation (the command, file path or pattern acted on) with their refs — "
          + "the direct evidence for repeated failed attempts. failures lists one row per failure with a head-and-tail excerpt of its error, "
          + "a page at a time: pass next_offset as offset for the next page, and a row's call_id (with its agent_id, when present) to get_tool_result for its full output.",
            new(
                "object",
                new() {
                    ["session_id"] = new("string", "Session ID (must match the judge's bound session)"),
                    ["offset"]     = new("integer", "Failure rows to skip (the previous page's next_offset)"),
                    ["limit"]      = new("integer", $"Failure rows per page (default {JudgeErrorDigest.DefaultLimit}, max {JudgeErrorDigest.MaxLimit})")
                },
                ["session_id"]
            ),
            McpToolAnnotations.Read
        ),
        new(
            "get_transcript",
            "Get the full transcript of a specific session: user messages, assistant reasoning, tool calls, and results. Paginated (default 100 events). Use file_path filter to scope to events mentioning a specific file. This is the deepest level of detail — use when you need to trace the exact reasoning chain.",
            new(
                "object",
                new() {
                    ["session_id"] = new("string", "Session ID to retrieve the transcript for (must match the judge's bound session)"),
                    ["file_path"]  = new("string", "Optional file path to filter transcript events"),
                    ["skip"]       = new("integer", "Number of events to skip (for pagination)"),
                    ["take"]       = new("integer", "Number of events to return (for pagination)")
                },
                ["session_id"]
            ),
            McpToolAnnotations.Read
        ),
        new(
            "get_session_summary",
            "Get a structured orientation digest for the session: title, turn count, plan text, files touched (path + change type + event count), "
          + "test runs with pass/fail, error flag, and which tool results were truncated in the compacted trace (pair call_ids with get_tool_result). "
          + "START HERE when you need a quick overview before deciding which MCP tool to call next. A long summary comes back a page at a time.",
            new(
                "object",
                new() {
                    ["session_id"] = new("string", "Session ID (must match the judge's bound session)"),
                    ["offset"]     = new("integer", "Character offset to continue a long result from (the offset its last page names)")
                },
                ["session_id"]
            ),
            McpToolAnnotations.Read
        ),
        new(
            "search_session",
            "Full-text search scoped to one session. Returns ranked excerpts with speaker (user/assistant/tool), timestamp, and a highlighted snippet. "
          + "Use for targeted lookups ('where did the agent run rm', 'what error mentioned permission') rather than paging the full trace. "
          + "A long snippet is cut to a window around the first query term. limit defaults to 20, capped at 50.",
            new(
                "object",
                new() {
                    ["session_id"] = new("string", "Session ID (must match the judge's bound session)"),
                    ["query"]      = new("string", "Free-text query: words, \"quoted phrases\", or, -excluded"),
                    ["limit"]      = new("integer", "Max number of ranked excerpts to return (default 20, max 50)")
                },
                ["session_id", "query"]
            ),
            McpToolAnnotations.Read
        ),
        new(
            "get_tool_result",
            "Fetch the untruncated body of a single tool result by its call_id. Use when get_session_summary reported the result was truncated "
          + "(see the truncated_tool_results field) and the finding depends on what the tool actually produced — e.g. full dotnet test output, "
          + $"full git diff, full search hit list. An output longer than {JudgeResultCap.MaxChars} characters comes back a page at a time; "
          + "pass its next_offset as offset to read the rest.",
            new(
                "object",
                new() {
                    ["session_id"] = new("string", "Session ID (must match the judge's bound session)"),
                    ["call_id"]    = new("string", "call_id from a tool_invocation/tool_result pair (e.g. toolu_01abc...)"),
                    ["agent_id"]   = new("string", "The subagent whose call it was, as a get_session_errors row names it"),
                    ["offset"]     = new("integer", "Character offset into the output (default 0)")
                },
                ["session_id", "call_id"]
            ),
            McpToolAnnotations.Read
        )
    ];

    static SessionSearchQuery BuildSearchBody(JsonObject? arguments) {
        var query = GetRequiredString(arguments, "query");

        int? limit = null;

        if (arguments?["limit"] is { } limitNode) {
            try {
                limit = limitNode.GetValue<int>();
            } catch (InvalidOperationException) {
                throw new ArgumentException("limit must be an integer");
            } catch (FormatException) {
                throw new ArgumentException("limit must be an integer");
            }
        }

        return new(query, limit);
    }

    static string GetRequiredString(JsonObject? arguments, string name) {
        var value = arguments?[name]?.GetValue<string>();

        return string.IsNullOrWhiteSpace(value) ? throw new ArgumentException($"Missing required argument: {name}") : value;
    }

    static string BuildToolResultUrl(string apiRoot, string encodedSessionId, JsonObject? arguments) {
        var url = $"{apiRoot}/api/sessions/{encodedSessionId}/tool-results/{Uri.EscapeDataString(GetRequiredString(arguments, "call_id"))}";
        return arguments?["agent_id"]?.GetValue<string>() is { Length: > 0 } agentId ? $"{url}?agent_id={Uri.EscapeDataString(agentId)}" : url;
    }

    static string BuildTranscriptUrl(string baseUrl, string sessionId, JsonObject? arguments) {
        var url         = $"{baseUrl}/api/review/sessions/{Uri.EscapeDataString(sessionId)}/transcript";
        var queryParams = new List<string>();

        if (arguments?["file_path"]?.GetValue<string>() is { } filePath) {
            queryParams.Add($"file_path={Uri.EscapeDataString(filePath)}");
        }

        if (arguments?["skip"]?.ToString() is { } skip) {
            queryParams.Add($"skip={Uri.EscapeDataString(skip)}");
        }

        if (arguments?["take"]?.ToString() is { } take) {
            queryParams.Add($"take={Uri.EscapeDataString(take)}");
        }

        if (queryParams.Count > 0) {
            url += "?" + string.Join("&", queryParams);
        }

        return url;
    }
}
