using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;

namespace Capacitor.Cli.Commands;

/// <summary>
/// `kcap mcp analytics` — governed SQL analytics over the server's read-model views.
/// Two read-only tools wrapping the bearer-authed /api/analytics endpoints: the agent
/// fetches the governed schema document, writes SQL itself, and self-repairs from the
/// server's machine-consumable rejection reasons. Structure cloned from McpMemoryServer.
/// </summary>
static class McpAnalyticsServer {
    internal const string NotLoggedInMessage = "Not logged in. Run 'kcap login' on the host shell.";

    internal const string NotSupportedMessage =
        "This server does not support analytics queries — upgrade kcap-server.";

    public static async Task<int> RunAsync(string baseUrl) {
        var cwdRepoHash = await ResolveCwdRepoHashAsync();
        var tools       = BuildToolsList();

        var urlOk = HttpClientExtensions.IsAcceptableUrl(baseUrl);

        // Deferred authenticated client — kcap-analytics auto-registers, so it is spawned for
        // every session; startup must stay local-only for sessions that never invoke a tool.
        // See McpMemoryServer for the nullable-field-not-Lazy rationale.
        HttpClient? client = null;

        async Task<string> DispatchToolCallAsync(JsonNode callId, JsonObject callRequest) {
            if (!urlOk)
                return BuildToolResult(callId, HttpClientExtensions.SchemeMissingHint, isError: true);

            try {
                client ??= await HttpClientExtensions.CreateAuthenticatedClientAsync(baseUrl);
                return await HandleToolCallAsync(callId, callRequest, client, baseUrl, cwdRepoHash);
            } catch (Exception ex) {
                await Console.Error.WriteLineAsync($"kcap mcp analytics: unexpected error handling tools/call: {ex}");
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
                    continue; // skip malformed JSON
                }

                if (request is null) continue;

                var id     = request["id"];
                var method = request["method"]?.GetValue<string>();

                if (id is null) continue; // notification — no response

                var response = method switch {
                    "initialize" => BuildInitializeResponse(id, request),
                    "tools/list" => BuildToolsListResponse(id, tools),
                    "tools/call" => await DispatchToolCallAsync(id, request),
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

    static async Task<string?> ResolveCwdRepoHashAsync() {
        try {
            var cwd      = Directory.GetCurrentDirectory();
            var repoInfo = await RepositoryDetection.DetectRepositoryAsync(cwd);

            if (repoInfo?.Owner is null || repoInfo.RepoName is null) return null;

            return RepoHashHelper.ComputeRepoHash(repoInfo.Owner, repoInfo.RepoName);
        } catch {
            return null;
        }
    }

    const string ServerInstructions =
        "Query Kurrent Capacitor's AI coding-agent analytics (sessions, tool/skill/token usage, cost, " +
        "commits, PRs, evals) with governed read-only SQL. Call get_analytics_schema once to learn the " +
        "views, then write Postgres SELECTs for query_analytics. Rejections include the reason — fix the " +
        "SQL and retry.";

    static string BuildInitializeResponse(JsonNode id, JsonObject request) =>
        ToResponse<McpInitResult>(
            id,
            new(McpProtocol.NegotiateVersion(request), new(new()), new("kcap-analytics", "1.0.0"), ServerInstructions),
            McpJsonContext.Default.McpInitResult
        );

    static string BuildToolsListResponse(JsonNode id, McpTool[] tools) =>
        ToResponse(id, new McpToolsResult(tools), McpJsonContext.Default.McpToolsResult);

    internal static async Task<string> HandleToolCallAsync(
            JsonNode   id,
            JsonObject request,
            HttpClient client,
            string     baseUrl,
            string?    cwdRepoHash
        ) {
        var paramsNode = request["params"]?.AsObject();
        var toolName   = paramsNode?["name"]?.GetValue<string>();
        var arguments  = paramsNode?["arguments"]?.AsObject();

        if (toolName is null) {
            return BuildErrorResponse(id, -32602, "Missing params.name");
        }

        try {
            using var httpResponse = toolName switch {
                "get_analytics_schema" => await SendWithRefreshRetryAsync(client, c => c.GetAsync($"{baseUrl}/api/analytics/schema")),
                "query_analytics"      => await SendWithRefreshRetryAsync(client, c => c.PostAsync($"{baseUrl}/api/analytics/query", ToJsonContent(BuildQueryBody(arguments, cwdRepoHash)))),
                _                      => throw new ArgumentException($"Unknown tool: {toolName}")
            };

            var body = await httpResponse.Content.ReadAsStringAsync();

            return BuildToolResult(id, MapResponse(toolName, httpResponse.StatusCode, body, out var isError), isError);
        } catch (ArgumentException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        } catch (HttpRequestException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        }
    }

    /// <summary>Maps an /api/analytics response to the tool result text. Success bodies pass
    /// through as raw JSON (plus an explicit truncation trailer — a boolean field alone is easy
    /// for a model to miss); error statuses become actionable, self-repair-friendly messages.</summary>
    internal static string MapResponse(string toolName, HttpStatusCode status, string body, out bool isError) {
        isError = true;

        switch (status) {
            case HttpStatusCode.OK:
                isError = false;
                return toolName == "get_analytics_schema" ? ExtractSchemaText(body) : AppendTruncationTrailer(body);
            case HttpStatusCode.Unauthorized:
                return NotLoggedInMessage;
            case HttpStatusCode.NotFound:
                return NotSupportedMessage; // pre-AI-1470 server without /api/analytics
            case HttpStatusCode.RequestTimeout:
                return "Query timed out — narrow the date range or aggregate.";
            case HttpStatusCode.BadRequest:
                return $"REJECTED: {ExtractProblemDetail(body)}";
            default:
                return $"Error: HTTP {(int)status} — {body}";
        }
    }

    /// <summary>The schema endpoint wraps the document in {"text", "max_rows"} — unwrap it so the
    /// agent receives the document itself, not a JSON envelope.</summary>
    static string ExtractSchemaText(string body) {
        try {
            return JsonNode.Parse(body)?.AsObject()?["text"]?.GetValue<string>() ?? body;
        } catch {
            return body;
        }
    }

    static string AppendTruncationTrailer(string body) {
        try {
            var obj = JsonNode.Parse(body)?.AsObject();

            if (obj?["truncated"]?.GetValue<bool>() == true) {
                var maxRows = obj["max_rows"]?.GetValue<int>();
                return $"{body}\n(truncated to {maxRows?.ToString() ?? "the server's"} rows — add filters or aggregate)";
            }
        } catch {
            // fall through to the raw body
        }

        return body;
    }

    /// <summary>400 bodies are RFC-7807 problem documents; the `detail` field carries the
    /// validator's machine-consumable rejection reason the agent self-repairs from.</summary>
    static string ExtractProblemDetail(string body) {
        try {
            return JsonNode.Parse(body)?.AsObject()?["detail"]?.GetValue<string>() ?? body;
        } catch {
            return body;
        }
    }

    static async Task<HttpResponseMessage> SendWithRefreshRetryAsync(HttpClient client, Func<HttpClient, Task<HttpResponseMessage>> send) {
        var response = await send(client);

        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;

        var refreshed = await TokenStore.GetValidTokensAsync();

        if (refreshed is null) return response; // genuinely not logged in; keep the original 401

        response.Dispose();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", refreshed.AccessToken);

        return await send(client);
    }

    static StringContent ToJsonContent(JsonObject body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    // NOTE: request-body keys are snake_case — the server's global JSON policy.
    internal static JsonObject BuildQueryBody(JsonObject? args, string? cwdRepoHash) {
        var sql = args?["sql"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentException("sql is required");

        var scope = args?["scope"]?.GetValue<string>() ?? "repo";

        // Fail closed rather than silently widening scope: an unresolvable cwd repo must never
        // default to the tenant-wide route (mirrors McpMemoryServer.BuildSaveBody).
        var repos = scope switch {
            "repo" when cwdRepoHash is null => throw new ArgumentException(
                "Cannot resolve the current repository — run from a git checkout or pass scope: 'global' to query all repositories."),
            "repo"   => new JsonArray(JsonValue.Create(cwdRepoHash)),
            "global" => null,
            _        => throw new ArgumentException($"Unknown scope: '{scope}' — use 'repo' (default) or 'global'.")
        };

        var body = new JsonObject {
            ["sql"]   = sql,
            ["repos"] = repos
        };

        if (McpMemoryServer.TryReadInt(args, "max_rows", out var maxRows)) body["max_rows"] = maxRows;

        return body;
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
        new("get_analytics_schema",
            "The governed analytics schema: the v_an_* views and columns you may query, a terminology glossary, " +
            "SQL rules, and worked examples. Always call this once before writing SQL for query_analytics. " +
            "Covers sessions, tool/skill/token usage, cost, code changes, commits, PRs, evals, and work items.",
            new("object", new(), [])),
        new("query_analytics",
            "Run one governed read-only Postgres SELECT over the analytics views (see get_analytics_schema). " +
            "Answers questions about coding-agent sessions, usage, cost, and delivery telemetry. A rejected " +
            "query returns the reason — fix the SQL and retry. Results are row-capped and flagged when truncated.",
            new("object", new() {
                ["sql"]      = new("string", "A single Postgres SELECT over the governed v_an_* views."),
                ["scope"]    = new("string", "'repo' (default) = only the current repository, derived from the working directory; 'global' = all repositories in the org."),
                ["max_rows"] = new("number", "Row cap for this query (default 300; clamped to the server's maximum).")
            }, ["sql"]))
    ];
}
