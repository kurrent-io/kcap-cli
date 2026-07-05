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

/// <summary>
/// AI-1139: reviewer-side MCP server injected into hosted review-flow launches. Exposes a
/// single submit_review_result tool that POSTs to /api/flows/reviewer/result. Deliberately
/// a SEPARATE command from `kcap mcp flows` — a hard security boundary so no flag regression
/// can ever expose start_review_flow to an unattended reviewer.
/// </summary>
static class McpFlowResultServer {
    internal const string AgentIdEnvVar = "KCAP_FLOW_AGENT_ID";

    const int MaxAttempts = 5;
    static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    // AI-1127 E-0 (AI-1190): the server no longer reads the transcript — markers deliver
    // nothing, so the only useful guidance on failure is to retry the tool itself.
    const string FallbackHint =
        "Retry this tool call — it is the ONLY delivery channel. Do NOT fall back to FINDINGS:/NO FINDINGS markers in your reply: the server does not read the transcript, so a marker delivers nothing.";

    public static async Task<int> RunAsync(string baseUrl) {
        var agentId = Environment.GetEnvironmentVariable(AgentIdEnvVar);

        if (string.IsNullOrWhiteSpace(agentId)) {
            await Console.Error.WriteLineAsync(
                $"kcap mcp flow-result: {AgentIdEnvVar} is not set. This server is launched by the kcap daemon for hosted review-flow reviewers; it is not meant to be run manually.");
            return 2;
        }

        var tools = BuildToolsList();

        // Validate the server_url shape once, locally (pure string check — no network, token,
        // or stderr). Used to fail gracefully instead of hard-exiting mid-request (below).
        var urlOk = HttpClientExtensions.IsAcceptableUrl(baseUrl);

        // The authenticated client is created on the first tools/call, not at startup — mirrors
        // McpFlowsServer/McpReviewServer: keeps startup local-only (no GET /auth/config, token
        // load, or stderr re-auth hint) for a server that only ever handles one tool call.
        // Created on demand into a nullable field (rather than a Lazy<Task>) so a transient
        // creation failure leaves it null and the next call retries, instead of a faulted task
        // sticking for the rest of the session. Safe without locking: the stdio loop handles
        // one request at a time.
        HttpClient? client = null;
        var apiRoot = baseUrl.TrimEnd('/');

        // Guarded tool dispatch: never let the stdio JSON-RPC loop die on one bad request. An
        // unusable server_url would otherwise reach EnsureAbsolute inside the auth-client factory,
        // which hard-exits the process (Environment.Exit(2)) mid-request; and an unexpected
        // failure would bubble out of the loop. Return a JSON-RPC tool error in both cases so the
        // server keeps serving.
        async Task<string> DispatchToolCallAsync(JsonNode callId, JsonObject callRequest) {
            if (!urlOk)
                return BuildToolResult(callId, HttpClientExtensions.SchemeMissingHint, isError: true);

            try {
                // Params extraction stays INSIDE the guard: a malformed tools/call (params
                // not an object, name not a string) must yield a JSON-RPC error, not throw
                // past the loop and kill the reviewer's only result-submission tool
                // (Qodo review on #240; matches McpFlowsServer/McpReviewServer structure).
                JsonObject? paramsNode;
                string?     toolName;
                JsonObject? arguments;

                try {
                    paramsNode = callRequest["params"]?.AsObject();
                    toolName   = paramsNode?["name"]?.GetValue<string>();
                    arguments  = paramsNode?["arguments"]?.AsObject();
                } catch (InvalidOperationException) {
                    return BuildErrorResponse(callId, -32602, "Invalid params");
                } catch (FormatException) {
                    return BuildErrorResponse(callId, -32602, "Invalid params");
                }

                if (toolName is null)
                    return BuildErrorResponse(callId, -32602, "Missing params.name");

                if (toolName != "submit_review_result")
                    return BuildToolResult(callId, $"Error: Unknown tool: {toolName}", isError: true);

                client ??= await HttpClientExtensions.CreateAuthenticatedClientAsync(baseUrl);

                var (text, isError) = await SubmitCoreAsync(
                    client,
                    apiRoot,
                    agentId,
                    arguments,
                    delay: Task.Delay
                );

                return BuildToolResult(callId, text, isError);
            } catch (Exception ex) {
                // Unexpected: log the detail to stderr (not to the client, which could leak local
                // paths from IO errors) and return a generic tool error, keeping the loop alive.
                await Console.Error.WriteLineAsync($"kcap mcp flow-result: unexpected error handling tools/call: {ex}");
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

    /// <summary>Validation + POST + retry policy. Injectable delay so tests run instantly.
    /// Returns the tool text and error flag; never throws for expected failures.</summary>
    internal static async Task<(string Text, bool IsError)> SubmitCoreAsync(
            HttpClient           client,
            string               apiRoot,
            string               agentId,
            JsonObject?          arguments,
            Func<TimeSpan, Task> delay
        ) {
        var roundToken = arguments?["round_token"]?.GetValue<string>();
        var kind       = arguments?["kind"]?.GetValue<string>();
        var findings   = arguments?["findings"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(roundToken))
            return ("Error: round_token is required — copy it from the \"round token\" in your prompt.", true);
        if (kind is not ("findings" or "clean"))
            return ("Error: kind must be \"findings\" or \"clean\".", true);
        if (kind == "findings" && string.IsNullOrWhiteSpace(findings))
            return ("Error: findings text is required when kind is \"findings\".", true);

        var body = new SubmitReviewerResultDto(agentId, roundToken, kind, kind == "findings" ? findings : null);
        var url  = $"{apiRoot.TrimEnd('/')}/api/flows/reviewer/result";

        for (var attempt = 1; attempt <= MaxAttempts; attempt++) {
            using var response = await SendWithRefreshRetryAsync(
                client,
                c => c.PostAsync(url, JsonContent.Create(body, McpJsonContext.Default.SubmitReviewerResultDto))
            );
            var responseBody = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode) {
                var node        = TryParse(responseBody);
                var roundNumber = node?["round_number"]?.GetValue<int>();
                return (roundNumber is { } n
                    ? $"Result recorded for round {n}. You may end your reply now."
                    : "Result recorded. You may end your reply now.", false);
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return ("Not logged in. Run 'kcap login' on the host shell.", true);

            var errorNode = TryParse(responseBody);
            var code      = errorNode?["error"]?.GetValue<string>();
            var message   = errorNode?["message"]?.GetValue<string>() ?? responseBody;

            if (code is "no_active_flow" or "no_open_round") {
                // Launch race: the server's flow-assignment/round events may not be projected
                // yet when a fast reviewer submits. Retry inside the tool call.
                if (attempt < MaxAttempts) {
                    await delay(RetryDelay);
                    continue;
                }
                return ($"Error: {message}\n{FallbackHint}", true);
            }

            if (code == "stale_round_token")
                // Deliberately NO retry hint: a stale round token means this round is already
                // closed — the result must be discarded, never redelivered (spec-review round 2
                // finding; the AI-1139 round-token guard).
                return ($"Error: {message}", true);

            return ($"Error: HTTP {(int)response.StatusCode} — {message}\n{FallbackHint}", true);
        }

        return ("Error: unreachable", true); // loop always returns

        static JsonObject? TryParse(string s) {
            try { return JsonNode.Parse(s)?.AsObject(); } catch { return null; }
        }
    }

    /// <summary>
    /// Sends an HTTP request with one-shot retry on 401. See McpFlowsServer's copy of this
    /// helper for the full rationale: a cached token that was valid at startup may have
    /// expired by the time this single tool call is made, so on 401 we ask
    /// <see cref="TokenStore.GetValidTokensAsync"/> for a fresh token, update the client's
    /// <c>Authorization</c> header, and retry the same request once.
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

    static string BuildInitializeResponse(JsonNode id) =>
        ToResponse<McpInitResult>(
            id,
            new("2024-11-05", new(new()), new("kcap-flow-result", "1.0.0")),
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
            Name: "submit_review_result",
            Description: "Submit your review result for the current round. Call once. kind=\"findings\" with your findings text, or kind=\"clean\" when there are no actionable findings. round_token comes from the \"round token\" line in your prompt.",
            InputSchema: new McpInputSchema(
                Type: "object",
                Properties: new Dictionary<string, McpSchemaProperty> {
                    ["round_token"] = new("string", "The round token from your prompt (correlates this result to the round)."),
                    ["kind"]        = new("string", "\"findings\" or \"clean\"."),
                    ["findings"]    = new("string", "Your findings text; required when kind is \"findings\".")
                },
                Required: ["round_token", "kind"]
            )
        )
    ];
}

/// <summary>CLI-side DTO for POST /api/flows/reviewer/result — mirrors the server's SubmitReviewerResultRequest.</summary>
record SubmitReviewerResultDto(
    [property: JsonPropertyName("agent_id")]    string  AgentId,
    [property: JsonPropertyName("round_token")] string  RoundToken,
    [property: JsonPropertyName("kind")]        string  Kind,
    [property: JsonPropertyName("text")]        string? Text
);
