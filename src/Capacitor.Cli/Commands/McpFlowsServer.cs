using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Commands;

static class McpFlowsServer {
    public static async Task<int> RunAsync(string baseUrl) {
        using var client = await HttpClientExtensions.CreateAuthenticatedClientAsync(baseUrl);

        var cwd      = Directory.GetCurrentDirectory();
        var repoRoot = GitRepository.FindRoot(cwd);
        var tools    = BuildToolsList();

        RepositoryPayload? repoInfo = null;
        try {
            repoInfo = await RepositoryDetection.DetectRepositoryAsync(cwd);
        } catch {
            // best-effort; proceed with null
        }

        await using var stdin  = Console.OpenStandardInput();
        await using var stdout = Console.OpenStandardOutput();
        using var       reader = new StreamReader(stdin, Encoding.UTF8);
        await using var writer = new StreamWriter(stdout, new UTF8Encoding(false));
        writer.AutoFlush = true;

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
                "tools/call" => await HandleToolCallAsync(id, request, client, baseUrl, cwd, repoRoot, repoInfo),
                _            => BuildErrorResponse(id, -32601, $"Method not found: {method}")
            };

            await writer.WriteLineAsync(response);
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

            using var httpResponse = toolName switch {
                "start_review_flow"      => await StartReviewFlowAsync(client, apiRoot, arguments, cwd, repoRoot, repoInfo),
                "submit_review_round"    => await SubmitReviewRoundAsync(client, apiRoot, arguments),
                "get_review_flow_status" => await client.GetAsync(BuildFlowUrl(apiRoot, arguments)),
                "close_review_flow"      => await client.PostAsync(BuildFlowUrl(apiRoot, arguments) + "/close", null),
                _                        => throw new ArgumentException($"Unknown tool: {toolName}")
            };

            var body = await httpResponse.Content.ReadAsStringAsync();

            if (httpResponse.StatusCode == HttpStatusCode.Unauthorized) {
                return BuildToolResult(id, "Not logged in. Run 'kcap login' on the host shell.", isError: true);
            }

            if (!httpResponse.IsSuccessStatusCode) {
                return BuildToolResult(id, $"Error: HTTP {(int)httpResponse.StatusCode} — {body}", isError: true);
            }

            var payload = toolName switch {
                "get_review_flow_status" => FormatStatusResponse(body),
                "close_review_flow"      => FormatCloseResponse(body),
                _                        => FormatRoundResponse(body)
            };

            return BuildToolResult(id, payload);
        } catch (ArgumentException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        } catch (HttpRequestException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        }
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
            Mode:                 mode
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

        var body = new SubmitReviewRoundDto(Context: context, Instructions: instructions);

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
            "Start a new review flow. The server starts an AI reviewer agent that will analyse the target and return findings. " +
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
            "Submit a follow-up round to an existing review flow. Use this to ask for clarifications, provide additional context, or request a re-review after addressing feedback. Returns the new round's findings.",
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
    [property: JsonPropertyName("mode")]                   string? Mode
);

/// <summary>CLI-side DTO for POST /api/flows/{flowRunId}/rounds — mirrors the server's SubmitReviewRoundRequest.</summary>
record SubmitReviewRoundDto(
    [property: JsonPropertyName("context")]      string  Context,
    [property: JsonPropertyName("instructions")] string? Instructions
);
