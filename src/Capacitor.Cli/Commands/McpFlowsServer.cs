using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Commands;

static class McpFlowsServer {
    public static async Task<int> RunAsync(string baseUrl) {
        var cwd      = Directory.GetCurrentDirectory();
        var repoRoot = GitRepository.FindRoot(cwd);
        var tools    = BuildToolsList();

        RepositoryPayload? repoInfo = null;
        try {
            repoInfo = await RepositoryDetection.DetectRepositoryAsync(cwd);
        } catch {
            // best-effort; proceed with null
        }

        // Validate the server_url shape once, locally (pure string check — no network, token,
        // or stderr). Used to fail gracefully instead of hard-exiting mid-request (below).
        var urlOk = HttpClientExtensions.IsAcceptableUrl(baseUrl);

        // The authenticated client is created on the first tools/call, not at startup: kcap-flows
        // auto-registers (AI-1056), so Claude Code spawns `kcap mcp flows` for every session —
        // deferring keeps startup local-only (no GET /auth/config, token load, or stderr re-auth
        // hint) for sessions that never invoke a flows tool. Created on demand into a nullable
        // field (rather than a Lazy<Task>) so a transient creation failure leaves it null and the
        // next call retries, instead of a faulted task sticking for the rest of the session. Safe
        // without locking: the stdio loop handles one request at a time.
        HttpClient? client = null;

        // Guarded tool dispatch: never let the stdio JSON-RPC loop die on one bad request. An
        // unusable server_url would otherwise reach EnsureAbsolute inside the auth-client factory,
        // which hard-exits the process (Environment.Exit(2)) mid-request; and an unexpected
        // failure would bubble out of the loop. Return a JSON-RPC tool error in both cases so the
        // server keeps serving.
        async Task<string> DispatchToolCallAsync(JsonNode callId, JsonObject callRequest) {
            if (!urlOk)
                return BuildToolResult(callId, HttpClientExtensions.SchemeMissingHint, isError: true);

            try {
                if (client is null) {
                    client = await HttpClientExtensions.CreateAuthenticatedClientAsync(baseUrl);
                    // AI-1061: the review-flow endpoints long-poll (start_review_flow /
                    // submit_review_round block server-side up to ~10 min while the reviewer runs).
                    // The default 100s timeout would abort the POST, which the server sees as a
                    // cancel and tears the reviewer down — so disable the client-side deadline and
                    // let the server's FlowResultWaiter + the harness MCP tool timeout bound it.
                    client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
                }

                return await HandleToolCallAsync(callId, callRequest, client, baseUrl, cwd, repoRoot, repoInfo);
            } catch (Exception ex) {
                // Unexpected: log the detail to stderr (not to the client, which could leak local
                // paths from IO errors) and return a generic tool error, keeping the loop alive.
                await Console.Error.WriteLineAsync($"kcap mcp flows: unexpected error handling tools/call: {ex}");
                return BuildToolResult(callId, "Error: internal error handling the request.", isError: true);
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
                var method = request["method"]?.GetValue<string>();

                // Notifications have no id — don't send a response
                if (id is null) continue;

                var response = method switch {
                    "initialize" => BuildInitializeResponse(id),
                    "tools/list" => BuildToolsListResponse(id, tools),
                    "tools/call" => await DispatchToolCallAsync(id, request),
                    _            => BuildErrorResponse(id, -32601, $"Method not found: {method}")
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

    static async Task<string> HandleToolCallAsync(
            JsonNode            id,
            JsonObject          request,
            HttpClient          client,
            string              baseUrl,
            string              cwd,
            string?             repoRoot,
            RepositoryPayload?  repoInfo
        ) {
        var paramsNode = request["params"]?.AsObject();
        var toolName   = paramsNode?["name"]?.GetValue<string>();
        var arguments  = paramsNode?["arguments"]?.AsObject();

        if (toolName is null) {
            return BuildErrorResponse(id, -32602, "Missing params.name");
        }

        try {
            var apiRoot = baseUrl.TrimEnd('/');

            // start_review_flow and submit_review_round may need async poll — handle separately.
            if (toolName is "start_review_flow" or "submit_review_round") {
                using var postResponse = toolName == "start_review_flow"
                    ? await SendWithRefreshRetryAsync(client, c => StartReviewFlowAsync(c, apiRoot, arguments, cwd, repoRoot, repoInfo))
                    : await SendWithRefreshRetryAsync(client, c => SubmitReviewRoundAsync(c, apiRoot, arguments));

                var postBody = await postResponse.Content.ReadAsStringAsync();

                if (postResponse.StatusCode == HttpStatusCode.Unauthorized)
                    return BuildToolResult(id, "Not logged in. Run 'kcap login' on the host shell.", isError: true);

                if (!postResponse.IsSuccessStatusCode)
                    return BuildToolResult(id, $"Error: HTTP {(int)postResponse.StatusCode} — {postBody}", isError: true);

                var (payload, isError) = await ResolveRoundResultAsync(client, apiRoot, postBody);
                return BuildToolResult(id, payload, isError);
            }

            using var httpResponse = toolName switch {
                "get_review_flow_status" => await SendWithRefreshRetryAsync(client, c => c.GetAsync(BuildFlowUrl(apiRoot, arguments))),
                "close_review_flow"      => await SendWithRefreshRetryAsync(client, c => c.PostAsync(BuildFlowUrl(apiRoot, arguments) + "/close", null)),
                _                        => throw new ArgumentException($"Unknown tool: {toolName}")
            };

            var body = await httpResponse.Content.ReadAsStringAsync();

            if (httpResponse.StatusCode == HttpStatusCode.Unauthorized) {
                return BuildToolResult(id, "Not logged in. Run 'kcap login' on the host shell.", isError: true);
            }

            if (!httpResponse.IsSuccessStatusCode) {
                return BuildToolResult(id, $"Error: HTTP {(int)httpResponse.StatusCode} — {body}", isError: true);
            }

            var statusPayload = toolName switch {
                "get_review_flow_status" => FormatStatusResponse(body),
                "close_review_flow"      => FormatCloseResponse(body),
                _                        => FormatRoundResponse(body)
            };

            return BuildToolResult(id, statusPayload);
        } catch (ArgumentException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        } catch (HttpRequestException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        }
    }

    /// <summary>
    /// Sends an HTTP request with one-shot retry on 401. The MCP server reuses a single
    /// <see cref="HttpClient"/> for the lifetime of the agent session, so a cached token
    /// that was valid at startup may have expired by the time a tool call is made. On 401
    /// we ask <see cref="TokenStore.GetValidTokensAsync"/> for a fresh token (which triggers
    /// the refresh flow for WorkOS / GitHubApp), update the client's <c>Authorization</c>
    /// header, and retry the same request once. If refresh fails (genuinely not logged in
    /// or refresh-token expired), the original 401 is returned and the caller surfaces the
    /// friendly "Not logged in" message.
    /// </summary>
    static async Task<HttpResponseMessage> SendWithRefreshRetryAsync(HttpClient client, Func<HttpClient, Task<HttpResponseMessage>> send) {
        var response = await send(client);

        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

        var refreshed = await TokenStore.GetValidTokensAsync();

        if (refreshed is null) return response; // genuinely not logged in; keep the original 401

        response.Dispose();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", refreshed.AccessToken);

        return await send(client);
    }

    static async Task<System.Net.Http.HttpResponseMessage> StartReviewFlowAsync(
            HttpClient         client,
            string             apiRoot,
            JsonObject?        arguments,
            string             cwd,
            string?            repoRoot,
            RepositoryPayload? repoInfo
        ) {
        var kind         = GetRequiredArg(arguments, "kind");
        var targetKind   = GetRequiredArg(arguments, "target_kind");
        var targetRef    = GetRequiredArg(arguments, "target_ref");
        var targetTitle  = GetRequiredArg(arguments, "target_title");
        var context      = GetRequiredArg(arguments, "context");
        var instructions = arguments?["instructions"]?.GetValue<string>();
        var mode         = arguments?["mode"]?.GetValue<string>();

        var sessionId = ArgParsing.ResolveSessionIdFromEnv();

        var body = new StartReviewFlowDto(
            Kind:                 kind,
            TargetKind:           targetKind,
            TargetRef:            targetRef,
            TargetTitle:          targetTitle,
            Context:              context,
            Instructions:         instructions,
            RequestingSessionId:  sessionId,
            RequestingCwd:        cwd,
            RequestingRepoRoot:   repoRoot,
            RepoOwner:            repoInfo?.Owner,
            RepoName:             repoInfo?.RepoName,
            DaemonName:           null,
            RepoPath:             repoRoot,
            Mode:                 mode,
            Async:                true
        );

        return await client.PostAsync(
            $"{apiRoot}/api/flows/review/start",
            JsonContent.Create(body, McpJsonContext.Default.StartReviewFlowDto)
        );
    }

    static Task<System.Net.Http.HttpResponseMessage> SubmitReviewRoundAsync(
            HttpClient  client,
            string      apiRoot,
            JsonObject? arguments
        ) {
        var flowRunId    = arguments?["flow_run_id"]?.GetValue<string>();
        var context      = GetRequiredArg(arguments, "context");
        var instructions = arguments?["instructions"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(flowRunId)) {
            throw new ArgumentException(
                "Missing required argument: flow_run_id. " +
                "Pass the flow_run_id returned by start_review_flow."
            );
        }

        var body = new SubmitReviewRoundDto(Context: context, Instructions: instructions, Async: true);

        return client.PostAsync(
            $"{apiRoot}/api/flows/{Uri.EscapeDataString(flowRunId)}/rounds",
            JsonContent.Create(body, McpJsonContext.Default.SubmitReviewRoundDto)
        );
    }

    static string BuildFlowUrl(string apiRoot, JsonObject? arguments) {
        var flowRunId = arguments?["flow_run_id"]?.GetValue<string>()
            ?? throw new ArgumentException("Missing required argument: flow_run_id");

        return $"{apiRoot}/api/flows/{Uri.EscapeDataString(flowRunId)}";
    }

    static readonly TimeSpan PollInterval  = TimeSpan.FromSeconds(3);
    static readonly TimeSpan PollCap       = TimeSpan.FromMinutes(8);   // safely below MCP_TOOL_TIMEOUT
    static readonly TimeSpan PerGetTimeout = TimeSpan.FromSeconds(20);
    static readonly TimeSpan NotFoundGrace = TimeSpan.FromSeconds(10);

    static readonly HashSet<string> TerminalRoundStatuses =
        new(StringComparer.Ordinal) { "findings", "clean", "waiting", "unclear", "failed", "cancelled" };

    // Structured result from the poll path so callers can propagate isError correctly.
    record PollResult(string Payload, bool IsError);

    // Maximum consecutive transient failures (5xx / network / TLS) before giving up.
    const int MaxTransientRetries = 5;

    /// <summary>If the POST already carries a terminal result (old/blocking server), return it.
    /// Otherwise poll GET /api/flows/{id} until the started round is terminal (AI-1061).</summary>
    static async Task<PollResult> ResolveRoundResultAsync(HttpClient client, string apiRoot, string postBody) {
        var node      = JsonNode.Parse(postBody)?.AsObject();
        var status    = node?["status"]?.GetValue<string>();
        var flowRunId = node?["flow_run_id"]?.GetValue<string>();
        var roundNum  = node?["round_number"]?.GetValue<int>();

        if (status != "running" || flowRunId is null || roundNum is null)
            return new(FormatRoundResponse(postBody), false); // terminal-in-POST (old server) or unparseable

        return await PollUntilTerminalAsync(client, apiRoot, flowRunId, roundNum.Value);
    }

    static async Task<PollResult> PollUntilTerminalAsync(HttpClient client, string apiRoot, string flowRunId, int roundNumber) {
        var url                   = $"{apiRoot}/api/flows/{Uri.EscapeDataString(flowRunId)}";
        var pollStartedAt         = DateTimeOffset.UtcNow;
        var deadline              = pollStartedAt + PollCap;
        // Fix #3: anchor the 404 grace window to poll start, not to first-seen-404.
        var notFoundGraceDeadline = pollStartedAt + NotFoundGrace;
        var consecutiveTransient  = 0;
        var lastTransientError    = (string?)null;

        while (DateTimeOffset.UtcNow < deadline) {
            using var getCts = new CancellationTokenSource(PerGetTimeout);
            HttpResponseMessage resp;
            try {
                resp = await SendWithRefreshRetryAsync(client, c => c.GetAsync(url, getCts.Token));
            } catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) {
                // Fix #4: count network/TLS/timeout as transient; stop after budget.
                consecutiveTransient++;
                lastTransientError = ex.Message;
                if (consecutiveTransient > MaxTransientRetries)
                    return new($"Error: poll failed after {MaxTransientRetries} consecutive network errors: {lastTransientError}", true);
                await Task.Delay(PollInterval); continue;
            }

            using (resp) {
                if (resp.StatusCode == HttpStatusCode.NotFound) {
                    // Fix #3: 404 only gets the grace window anchored to poll start.
                    if (DateTimeOffset.UtcNow > notFoundGraceDeadline)
                        return new($"Error: flow_run_id {flowRunId} not found.", true);
                    await Task.Delay(PollInterval); continue;
                }

                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                    return new("Not logged in. Run 'kcap login' on the host shell.", true);

                // Fix #4: non-transient 4xx (e.g. 400, 403) fail immediately.
                var statusCode = (int)resp.StatusCode;
                if (statusCode is >= 400 and < 500) {
                    var errBody = await resp.Content.ReadAsStringAsync();
                    return new($"Error: HTTP {statusCode} — {errBody}", true);
                }

                // Fix #4: 5xx / other non-success counts toward the transient budget.
                if (!resp.IsSuccessStatusCode) {
                    consecutiveTransient++;
                    lastTransientError = $"HTTP {statusCode}";
                    if (consecutiveTransient > MaxTransientRetries)
                        return new($"Error: poll failed after {MaxTransientRetries} consecutive server errors: {lastTransientError}", true);
                    await Task.Delay(PollInterval); continue;
                }

                // Successful response — reset transient counter.
                consecutiveTransient = 0;
                lastTransientError   = null;

                var body      = await resp.Content.ReadAsStringAsync();
                var node      = JsonNode.Parse(body)?.AsObject();
                var rn        = node?["round_number"]?.GetValue<int>();
                var rs        = node?["round_status"]?.GetValue<string>();
                var runStatus = node?["status"]?.GetValue<string>();

                // Fix #1: run-level terminal stops the loop, but only return round result
                // when the projected round matches the one we submitted.
                if (runStatus is "closed" or "failed") {
                    if (rn == roundNumber && rs is not null && TerminalRoundStatuses.Contains(rs))
                        return new(FormatPolledRoundResult(node!, flowRunId), false);
                    // Run became terminal before our round produced a result — explicit error.
                    return new($"Error: review run {runStatus} before round {roundNumber} produced a result.", true);
                }

                // Only act on OUR round; an earlier projection may still show a prior round.
                if (rn == roundNumber && rs is not null && TerminalRoundStatuses.Contains(rs))
                    return new(FormatPolledRoundResult(node!, flowRunId), false);
            }
            await Task.Delay(PollInterval);
        }

        // Genuine 8-min cap: round still legitimately running.
        return new(
            $"Review still running for flow_run_id {flowRunId} (round {roundNumber}). " +
            "Call get_review_flow_status to retrieve the result when ready.",
            false
        );
    }

    /// <summary>Formats the terminal GET /api/flows/{id} response into the same envelope+text as FormatRoundResponse.</summary>
    static string FormatPolledRoundResult(JsonObject node, string flowRunId) {
        var roundNumber = node["round_number"]?.GetValue<int>();
        var resultKind  = node["round_result_kind"]?.GetValue<string>() ?? node["round_status"]?.GetValue<string>() ?? "";
        var resultText  = node["round_result_text"]?.GetValue<string>();

        var sb = new StringBuilder();
        sb.Append("flow_run_id: "); AppendLine(sb, flowRunId);
        if (roundNumber.HasValue) { sb.Append("round_number: "); sb.AppendLine(roundNumber.Value.ToString()); }
        sb.Append("status: ");      AppendLine(sb, node["status"]?.GetValue<string>() ?? "");
        sb.Append("result_kind: "); AppendLine(sb, resultKind);
        if (!string.IsNullOrEmpty(resultText)) { sb.AppendLine(); sb.Append(resultText); }
        return sb.ToString();
    }

    /// <summary>
    /// Formats a ReviewFlowRoundResponse or ReviewFlowStatusResponse (from start/submit) into a
    /// compact envelope followed by the result text.
    /// </summary>
    static string FormatRoundResponse(string body) {
        try {
            var node = JsonNode.Parse(body)?.AsObject();
            if (node is null) return body;

            var flowRunId   = node["flow_run_id"]?.GetValue<string>()   ?? "";
            var roundId     = node["round_id"]?.GetValue<string>()     ?? "";
            var status      = node["status"]?.GetValue<string>()      ?? "";
            var resultKind  = node["result_kind"]?.GetValue<string>()  ?? "";
            var resultText  = node["result_text"]?.GetValue<string>();

            var sb = new StringBuilder();
            sb.Append("flow_run_id: "); AppendLine(sb, flowRunId);
            sb.Append("round_id: ");    AppendLine(sb, roundId);
            sb.Append("status: ");      AppendLine(sb, status);
            sb.Append("result_kind: "); AppendLine(sb, resultKind);

            if (!string.IsNullOrEmpty(resultText)) {
                sb.AppendLine();
                sb.Append(resultText);
            }

            return sb.ToString();
        } catch {
            return body;
        }
    }

    static string FormatStatusResponse(string body) {
        try {
            var node = JsonNode.Parse(body)?.AsObject();
            if (node is null) return body;

            var flowRunId      = node["flow_run_id"]?.GetValue<string>()      ?? "";
            var status         = node["status"]?.GetValue<string>()         ?? "";
            var definitionId   = node["definition_id"]?.GetValue<string>()   ?? "";
            var targetTitle    = node["target_title"]?.GetValue<string>()    ?? "";
            var roundCount     = node["round_count"]?.GetValue<int>();
            var lastResultKind = node["last_result_kind"]?.GetValue<string>();
            var lastResultText = node["last_result_text"]?.GetValue<string>();

            var sb = new StringBuilder();
            sb.Append("flow_run_id: ");   AppendLine(sb, flowRunId);
            sb.Append("status: ");        AppendLine(sb, status);
            sb.Append("definition_id: "); AppendLine(sb, definitionId);
            sb.Append("target_title: ");  AppendLine(sb, targetTitle);

            if (roundCount.HasValue) {
                sb.Append("round_count: ");
                sb.AppendLine(roundCount.Value.ToString());
            }

            if (!string.IsNullOrEmpty(lastResultKind)) {
                sb.Append("result_kind: "); AppendLine(sb, lastResultKind);
            }

            if (!string.IsNullOrEmpty(lastResultText)) {
                sb.AppendLine();
                sb.Append(lastResultText);
            }

            return sb.ToString();
        } catch {
            return body;
        }
    }

    /// <summary>
    /// Formats a CloseReviewFlowResponse into a compact envelope.
    /// The server returns only <c>flow_run_id</c> and <c>status</c>.
    /// </summary>
    static string FormatCloseResponse(string body) {
        try {
            var node = JsonNode.Parse(body)?.AsObject();
            if (node is null) return body;

            var flowRunId = node["flow_run_id"]?.GetValue<string>() ?? "";
            var status    = node["status"]?.GetValue<string>()      ?? "";

            var sb = new StringBuilder();
            sb.Append("flow_run_id: "); AppendLine(sb, flowRunId);
            sb.Append("status: ");      AppendLine(sb, status);

            return sb.ToString();
        } catch {
            return body;
        }
    }

    static void AppendLine(StringBuilder sb, string value) => sb.AppendLine(value);

    static string GetRequiredArg(JsonObject? arguments, string name) {
        var value = arguments?[name]?.GetValue<string>();
        return value ?? throw new ArgumentException($"Missing required argument: {name}");
    }

    static string BuildInitializeResponse(JsonNode id) =>
        ToResponse<McpInitResult>(
            id,
            new("2024-11-05", new(new()), new("kcap-flows", "1.0.0")),
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

    static McpTool[] BuildToolsList() => [
        new(
            "start_review_flow",
            "Start a new review flow. This hands the work to a SEPARATE hosted reviewer agent and iterates to sign-off — it is NOT how you review something yourself. " +
            "Only call this when the user explicitly asked for a review *flow* / to submit for review; for an ordinary 'review my PR' or 'code review' request, review directly and do NOT call this tool. " +
            "Returns findings (same UX); the server runs the reviewer asynchronously and the CLI polls internally. " +
            "Returns a flow_run_id that identifies this review session — save it to call submit_review_round or get_review_flow_status later.",
            new(
                "object",
                new() {
                    ["kind"]         = new("string", "Review flow kind. Valid values: 'spec-review' (for specs and design documents), 'code-review' (for code changes and PRs)."),
                    ["target_kind"]  = new("string", "What is being reviewed: 'pr', 'branch', 'file', 'spec', 'plan', etc."),
                    ["target_ref"]   = new("string", "A reference to the target (PR URL, branch name, file path, etc.)."),
                    ["target_title"] = new("string", "Human-readable title for the target (PR title, spec name, etc.)."),
                    ["context"]      = new("string", "Background context for the reviewer: what to focus on, constraints, definition of done."),
                    ["instructions"] = new("string", "Optional additional instructions for the reviewer agent."),
                    ["mode"]         = new("string", "Optional. Pass 'context-only' to treat the submitted context/diff as authoritative. Required for code-review unless the reviewer runs in your exact repo checkout; omitting it will cause the server to reject the request with an error.")
                },
                ["kind", "target_kind", "target_ref", "target_title", "context"]
            )
        ),
        new(
            "submit_review_round",
            "Submit a follow-up round to an existing review flow. Returns findings (same UX); the server runs the reviewer asynchronously and the CLI polls internally. Use this to ask for clarifications, provide additional context, or request a re-review after addressing feedback.",
            new(
                "object",
                new() {
                    ["flow_run_id"]  = new("string", "Flow run ID returned by start_review_flow."),
                    ["context"]      = new("string", "Updated context or response to the reviewer's previous findings."),
                    ["instructions"] = new("string", "Optional instructions for this round.")
                },
                ["flow_run_id", "context"]
            )
        ),
        new(
            "get_review_flow_status",
            "Get the current status of a review flow: running, waiting, completed, or failed. Also surfaces the last result kind and result text.",
            new(
                "object",
                new() {
                    ["flow_run_id"] = new("string", "Flow run ID returned by start_review_flow.")
                },
                ["flow_run_id"]
            )
        ),
        new(
            "close_review_flow",
            "Close a review flow, marking it as complete. Call this after the review is done and the findings have been addressed.",
            new(
                "object",
                new() {
                    ["flow_run_id"] = new("string", "Flow run ID returned by start_review_flow.")
                },
                ["flow_run_id"]
            )
        )
    ];
}

/// <summary>CLI-side DTO for POST /api/flows/review/start — mirrors the server's StartReviewFlowRequest fields.</summary>
record StartReviewFlowDto(
    [property: JsonPropertyName("kind")]                   string  Kind,
    [property: JsonPropertyName("target_kind")]            string  TargetKind,
    [property: JsonPropertyName("target_ref")]             string  TargetRef,
    [property: JsonPropertyName("target_title")]           string  TargetTitle,
    [property: JsonPropertyName("context")]                string  Context,
    [property: JsonPropertyName("instructions")]           string? Instructions,
    [property: JsonPropertyName("requesting_session_id")] string? RequestingSessionId,
    [property: JsonPropertyName("requesting_cwd")]         string? RequestingCwd,
    [property: JsonPropertyName("requesting_repo_root")]   string? RequestingRepoRoot,
    [property: JsonPropertyName("repo_owner")]             string? RepoOwner,
    [property: JsonPropertyName("repo_name")]              string? RepoName,
    [property: JsonPropertyName("daemon_name")]            string? DaemonName,
    [property: JsonPropertyName("repo_path")]              string? RepoPath,
    [property: JsonPropertyName("mode")]                   string? Mode,
    [property: JsonPropertyName("async")]                  bool    Async
);

/// <summary>CLI-side DTO for POST /api/flows/{flowRunId}/rounds — mirrors the server's SubmitReviewRoundRequest.</summary>
record SubmitReviewRoundDto(
    [property: JsonPropertyName("context")]      string  Context,
    [property: JsonPropertyName("instructions")] string? Instructions,
    [property: JsonPropertyName("async")]        bool    Async
);
