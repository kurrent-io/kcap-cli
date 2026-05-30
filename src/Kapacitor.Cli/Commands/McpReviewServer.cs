using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Kapacitor.Cli.Core;
using Kapacitor.Cli.Core.Commands;

namespace Kapacitor.Cli.Commands;

static class McpReviewServer {
    /// <summary>
    /// Run with an explicit session-default PR (used by <c>kapacitor review &lt;pr&gt;</c>).
    /// Tool calls may still override the default by passing a <c>pr</c> argument.
    /// </summary>
    public static Task<int> RunAsync(string baseUrl, string owner, string repo, int prNumber)
        => RunCoreAsync(baseUrl, new PrIdentity(owner, repo, prNumber));

    /// <summary>
    /// Run without an explicit session default (used by the plugin's argless MCP
    /// registration). PR identity comes from each tool call's <c>pr</c> argument,
    /// or as a fallback from git auto-detection against the current branch.
    /// </summary>
    public static Task<int> RunAutoAsync(string baseUrl) => RunCoreAsync(baseUrl, null);

    static async Task<int> RunCoreAsync(string baseUrl, PrIdentity? startupDefault) {
        using var client = await HttpClientExtensions.CreateAuthenticatedClientAsync(baseUrl);

        // Compute the session default once: explicit startup args win; otherwise
        // try git auto-detect. Result is cached for the lifetime of the server
        // and used as the fallback when a tool call doesn't carry an explicit `pr`.
        var sessionDefault = startupDefault ?? await DetectPrFromGitAsync();

        var tools = BuildToolsList();

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
                continue; // skip malformed JSON
            }

            if (request is null) continue;

            var id     = request["id"];
            var method = request["method"]?.GetValue<string>();

            // Notifications have no id — don't send a response
            if (id is null) continue;

            var response = method switch {
                "initialize" => BuildInitializeResponse(id),
                "tools/list" => BuildToolsListResponse(id, tools),
                "tools/call" => await HandleToolCallAsync(id, request, client, baseUrl, sessionDefault),
                _            => BuildErrorResponse(id, -32601, $"Method not found: {method}")
            };

            await writer.WriteLineAsync(response);
        }

        return 0;
    }

    static async Task<PrIdentity?> DetectPrFromGitAsync() {
        try {
            var cwd      = Directory.GetCurrentDirectory();
            var repoInfo = await RepositoryDetection.DetectRepositoryAsync(cwd);

            if (repoInfo?.Owner is not null && repoInfo.RepoName is not null && repoInfo.PrNumber is not null) {
                return new PrIdentity(repoInfo.Owner, repoInfo.RepoName, repoInfo.PrNumber.Value);
            }

            return null;
        } catch {
            return null;
        }
    }

    static string BuildInitializeResponse(JsonNode id) =>
        ToResponse<McpInitResult>(
            id,
            new("2024-11-05", new(new()), new("kapacitor-review", "1.0.0")),
            McpJsonContext.Default.McpInitResult
        );

    static string BuildToolsListResponse(JsonNode id, McpTool[] tools) =>
        ToResponse(id, new McpToolsResult(tools), McpJsonContext.Default.McpToolsResult);

    static async Task<string> HandleToolCallAsync(
            JsonNode    id,
            JsonObject  request,
            HttpClient  client,
            string      baseUrl,
            PrIdentity? sessionDefault
        ) {
        var paramsNode = request["params"]?.AsObject();
        var toolName   = paramsNode?["name"]?.GetValue<string>();
        var arguments  = paramsNode?["arguments"]?.AsObject();

        if (toolName is null) {
            return BuildErrorResponse(id, -32602, "Missing params.name");
        }

        // get_transcript keys off session_id, not PR — skip PR resolution.
        if (toolName == "get_transcript") {
            return await DispatchAsync(id, toolName, arguments, client, baseUrl, pr: null);
        }

        var resolution = PrResolution.Resolve(arguments, sessionDefault);

        if (resolution.Identity is null) {
            return BuildToolResult(id, resolution.Error!, isError: true);
        }

        return await DispatchAsync(id, toolName, arguments, client, baseUrl, resolution.Identity);
    }

    static async Task<string> DispatchAsync(
            JsonNode    id,
            string      toolName,
            JsonObject? arguments,
            HttpClient  client,
            string      baseUrl,
            PrIdentity? pr
        ) {
        try {
            var prBase = pr is null ? null : $"{baseUrl}/api/review/{pr.Owner}/{pr.Repo}/pulls/{pr.PrNumber}";

            var httpResponse = toolName switch {
                "get_pr_summary"   => await client.GetAsync(prBase),
                "list_pr_files"    => await client.GetAsync($"{prBase}/files"),
                "get_file_context" => await client.GetAsync($"{prBase}/files/{GetRequiredArg(arguments, "file_path").TrimStart('/')}"),
                "search_context" => await client.PostAsync(
                    $"{prBase}/search",
                    JsonContent.Create(new(GetRequiredArg(arguments, "query")), McpJsonContext.Default.SearchQuery)
                ),
                "list_sessions"  => await client.GetAsync($"{prBase}/sessions"),
                "get_transcript" => await client.GetAsync(BuildTranscriptUrl(baseUrl, arguments)),
                _                => throw new ArgumentException($"Unknown tool: {toolName}")
            };

            var body = await httpResponse.Content.ReadAsStringAsync();

            return !httpResponse.IsSuccessStatusCode ? BuildToolResult(id, $"Error: HTTP {(int)httpResponse.StatusCode} — {body}", isError: true) : BuildToolResult(id, body);
        } catch (ArgumentException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        } catch (HttpRequestException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        }
    }

    static string GetRequiredArg(JsonObject? arguments, string name) {
        var value = arguments?[name]?.GetValue<string>();

        return value ?? throw new ArgumentException($"Missing required argument: {name}");
    }

    static string BuildTranscriptUrl(string baseUrl, JsonObject? arguments) {
        var sessionId = arguments?["session_id"]?.GetValue<string>()
         ?? throw new ArgumentException("Missing required argument: session_id");

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

    static McpTool[] BuildToolsList() {
        const string PrArgDescription =
            "Optional PR reference (e.g. 'owner/repo#123' or a github.com PR URL). " +
            "Defaults to the session's PR if launched via `kapacitor review`, otherwise auto-detected from current branch.";

        return [
            new(
                "get_pr_summary",
                "Get an overview of the PR: which Claude Code sessions contributed, which files were changed (with event counts), and what test commands were run with their pass/fail outcomes. Call this first to orient yourself.",
                new("object", new() { ["pr"] = new("string", PrArgDescription) }, [])
            ),
            new(
                "list_pr_files",
                "List all files changed in the PR with aggregated metadata: change types (read/edit/create), how many sessions touched each file, and total event count. Use this to understand the scope of changes.",
                new("object", new() { ["pr"] = new("string", PrArgDescription) }, [])
            ),
            new(
                "get_file_context",
                "Get deep context for a specific file: which sessions modified it, when, and relevant transcript excerpts where the file was discussed or changed. Use this when a reviewer asks 'why was this file changed?'",
                new(
                    "object",
                    new() {
                        ["file_path"] = new("string", "Path of the file to get context for"),
                        ["pr"]        = new("string", PrArgDescription)
                    },
                    ["file_path"]
                )
            ),
            new(
                "search_context",
                "Full-text search across all session transcripts linked to this PR. Returns ranked excerpts with speaker (user/assistant/tool), content, and highlighted snippets. Use for 'why' questions: 'why retry logic', 'what alternatives', 'error handling rationale'.",
                new(
                    "object",
                    new() {
                        ["query"] = new("string", "Free-text search query"),
                        ["pr"]    = new("string", PrArgDescription)
                    },
                    ["query"]
                )
            ),
            new(
                "list_sessions",
                "List all Claude Code sessions that contributed to this PR, with session IDs, titles, timestamps, and models used. Use this to understand the work timeline and pick sessions to drill into with get_transcript.",
                new("object", new() { ["pr"] = new("string", PrArgDescription) }, [])
            ),
            new(
                "get_transcript",
                "Get the full transcript of a specific session: user messages, assistant reasoning, tool calls, and results. Paginated (default 100 events). Use file_path filter to scope to events mentioning a specific file. This is the deepest level of detail — use when you need to trace the exact reasoning chain.",
                new(
                    "object",
                    new() {
                        ["session_id"] = new("string", "Session ID to retrieve the transcript for"),
                        ["file_path"]  = new("string", "Optional file path to filter transcript events"),
                        ["skip"]       = new("integer", "Number of events to skip (for pagination)"),
                        ["take"]       = new("integer", "Number of events to return (for pagination)")
                    },
                    ["session_id"]
                )
            )
        ];
    }
}

/// <summary>
/// PR identity resolved from one of: a tool call's <c>pr</c> argument, the
/// server's startup args, or git auto-detection at startup.
/// </summary>
record PrIdentity(string Owner, string Repo, int PrNumber);

/// <summary>
/// Resolves a tool call's effective PR identity. Tool args take precedence
/// over the session default. Returning a <c>null</c> identity carries an
/// error message ready to surface to the LLM.
/// </summary>
static class PrResolution {
    public readonly record struct Result(PrIdentity? Identity, string? Error);

    public static Result Resolve(JsonObject? toolArgs, PrIdentity? sessionDefault) {
        if (TryGetStringArg(toolArgs, "pr", out var prRef)) {
            if (PrRefParser.TryParse(prRef, out var owner, out var repo, out var prNumber)) {
                return new Result(new PrIdentity(owner, repo, prNumber), null);
            }

            return new Result(
                null,
                $"Could not parse `pr` argument: '{prRef}'. Use 'owner/repo#123' or a github.com PR URL."
            );
        }

        if (sessionDefault is not null) {
            return new Result(sessionDefault, null);
        }

        return new Result(
            null,
            "This tool needs a PR reference. Pass `pr` as a tool argument " +
            "(e.g. 'owner/repo#123' or a github.com PR URL), or run from a " +
            "branch with an open PR for auto-detection."
        );
    }

    static bool TryGetStringArg(JsonObject? args, string name, out string value) {
        value = "";

        if (args?[name] is not JsonValue node) return false;
        if (!node.TryGetValue<string>(out var s) || string.IsNullOrWhiteSpace(s)) return false;

        value = s;

        return true;
    }
}

// MCP protocol types — serialized with source-generated McpJsonContext for AOT compatibility
record McpInitResult(string ProtocolVersion, McpCapabilities Capabilities, McpServerInfo ServerInfo);

record McpCapabilities(McpToolsCapability Tools);

record McpToolsCapability;

record McpServerInfo(string Name, string Version);

record McpToolsResult(McpTool[] Tools);

record McpTool(string Name, string Description, McpInputSchema InputSchema);

record McpInputSchema(string Type, Dictionary<string, McpSchemaProperty> Properties, string[] Required);

record McpSchemaProperty(string Type, string Description);

record McpToolCallResult(McpContentItem[] Content, bool? IsError = null);

record McpContentItem(string Type, string Text);

record McpError(int Code, string Message);

record SearchQuery(string Query);

record SessionSearchQuery(string Query, int? Limit = null);

[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase
)]
[JsonSerializable(typeof(McpInitResult))]
[JsonSerializable(typeof(McpToolsResult))]
[JsonSerializable(typeof(McpToolCallResult))]
[JsonSerializable(typeof(McpError))]
[JsonSerializable(typeof(SearchQuery))]
[JsonSerializable(typeof(SessionSearchQuery))]
partial class McpJsonContext : JsonSerializerContext;
