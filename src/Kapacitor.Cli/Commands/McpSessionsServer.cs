using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Kapacitor.Cli.Core;

namespace Kapacitor.Cli.Commands;

static class McpSessionsServer {
    public static async Task<int> RunAsync(string baseUrl) {
        using var client      = await HttpClientExtensions.CreateAuthenticatedClientAsync(baseUrl);
        var       cwdRepoHash = await ResolveCwdRepoHashAsync();
        var       tools       = BuildToolsList();

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
                "tools/call" => await HandleToolCallAsync(id, request, client, baseUrl, cwdRepoHash),
                _            => BuildErrorResponse(id, -32601, $"Method not found: {method}")
            };

            await writer.WriteLineAsync(response);
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

    static string BuildInitializeResponse(JsonNode id) =>
        ToResponse<McpInitResult>(
            id,
            new("2024-11-05", new(new()), new("kapacitor-sessions", "1.0.0")),
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
                "search_sessions"        => await client.GetAsync(BuildSearchUrl(baseUrl, arguments, cwdRepoHash)),
                "get_session_summary"    => await client.GetAsync(BuildSummaryUrl(baseUrl, arguments)),
                "get_session_transcript" => await client.GetAsync(BuildTranscriptUrl(baseUrl, arguments)),
                _                        => throw new ArgumentException($"Unknown tool: {toolName}")
            };

            var body = await httpResponse.Content.ReadAsStringAsync();

            if (!httpResponse.IsSuccessStatusCode) {
                return BuildToolResult(id, $"Error: HTTP {(int)httpResponse.StatusCode} — {body}", isError: true);
            }

            // Client-side projection for get_session_summary: project /recap entries into { summary_text, plan }.
            var payload = toolName == "get_session_summary" ? ProjectRecapToSummary(body) : body;

            return BuildToolResult(id, payload);
        } catch (ArgumentException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        } catch (HttpRequestException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        }
    }

    /// <summary>
    /// Test hook: execute a tool-call handler and return the JSON-RPC envelope.
    /// </summary>
    internal static async Task<string> HandleToolCallForTests(
            string      toolName,
            JsonObject  arguments,
            HttpClient  client,
            string      baseUrl,
            string?     cwdRepoHash
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

        return await HandleToolCallAsync(id, request, client, baseUrl, cwdRepoHash);
    }

    internal static string BuildSearchUrl(string baseUrl, JsonObject? args, string? cwdRepoHash) {
        var url = $"{baseUrl}/api/sessions/search";
        var qs  = new List<string>();

        if (args?["query"]?.GetValue<string>() is { Length: > 0 } q) {
            qs.Add($"q={Uri.EscapeDataString(q)}");
        }

        if (args?["author"]?.GetValue<string>() is { Length: > 0 } author) {
            qs.Add($"author={Uri.EscapeDataString(author)}");
        }

        if (TryReadLong(args, "author_github_id", out var aid)) {
            qs.Add($"author_github_id={aid}");
        }

        // repo: explicit value > cwd-derived hash > omit. Sentinel "all" means cross-repo (omit param).
        var explicitRepo = args?["repo"]?.GetValue<string>();
        var repo         = explicitRepo ?? cwdRepoHash;

        if (repo is not null && !string.Equals(explicitRepo, "all", StringComparison.OrdinalIgnoreCase)) {
            qs.Add($"repo={Uri.EscapeDataString(repo)}");
        }

        if (TryReadInt(args, "limit", out var limit)) {
            qs.Add($"limit={limit}");
        }

        if (TryReadInt(args, "offset", out var offset)) {
            qs.Add($"offset={offset}");
        }

        return qs.Count == 0 ? url : url + "?" + string.Join("&", qs);
    }

    static string BuildSummaryUrl(string baseUrl, JsonObject? args) {
        var id = args?["session_id"]?.GetValue<string>()
              ?? throw new ArgumentException("Missing required argument: session_id");

        return $"{baseUrl}/api/sessions/{Uri.EscapeDataString(id)}/recap?chain=false";
    }

    static string BuildTranscriptUrl(string baseUrl, JsonObject? args) {
        var id = args?["session_id"]?.GetValue<string>()
              ?? throw new ArgumentException("Missing required argument: session_id");

        var url = $"{baseUrl}/api/sessions/{Uri.EscapeDataString(id)}/transcript";
        var qs  = new List<string>();

        if (TryReadInt(args, "around_event", out var a)) {
            qs.Add($"around_event={a}");
        }

        if (args?["agent_id"]?.GetValue<string>() is { Length: > 0 } aid) {
            qs.Add($"agent_id={Uri.EscapeDataString(aid)}");
        }

        if (TryReadInt(args, "before", out var b)) {
            qs.Add($"before={b}");
        }

        if (TryReadInt(args, "after", out var af)) {
            qs.Add($"after={af}");
        }

        if (TryReadInt(args, "limit", out var l)) {
            qs.Add($"limit={l}");
        }

        if (TryReadInt(args, "offset", out var o)) {
            qs.Add($"offset={o}");
        }

        if (TryReadBool(args, "chain", out var c)) {
            qs.Add($"chain={(c ? "true" : "false")}");
        }

        if (TryReadBool(args, "include_thinking", out var t)) {
            qs.Add($"include_thinking={(t ? "true" : "false")}");
        }

        return qs.Count == 0 ? url : url + "?" + string.Join("&", qs);
    }

    /// <summary>
    /// Reads a numeric field as int, tolerant of JsonValue holding any underlying numeric type
    /// (int/long/double) — TryGetValue&lt;int&gt; on a JsonValue constructed from a long returns false.
    /// </summary>
    static bool TryReadInt(JsonObject? args, string key, out int value) {
        value = 0;
        var node = args?[key];

        if (node is null) return false;

        try {
            var v = node.AsValue();
            if (v.TryGetValue<int>(out value)) return true;
            if (v.TryGetValue<long>(out var lv)) { value = checked((int)lv); return true; }
            if (v.TryGetValue<double>(out var dv)) { value = checked((int)dv); return true; }
        } catch {
            // overflow or wrong shape
        }

        return false;
    }

    static bool TryReadLong(JsonObject? args, string key, out long value) {
        value = 0;
        var node = args?[key];

        if (node is null) return false;

        try {
            var v = node.AsValue();
            if (v.TryGetValue<long>(out value)) return true;
            if (v.TryGetValue<int>(out var iv)) { value = iv; return true; }
            if (v.TryGetValue<double>(out var dv)) { value = (long)dv; return true; }
        } catch {
            // wrong shape
        }

        return false;
    }

    static bool TryReadBool(JsonObject? args, string key, out bool value) {
        value = false;
        var node = args?[key];

        if (node is null) return false;

        try {
            return node.AsValue().TryGetValue(out value);
        } catch {
            return false;
        }
    }

    /// <summary>
    /// Projects a /recap response (RecapEntry[]) into { summary_text, plan } for agent consumption.
    /// "Latest of type wins" — walks entries in order and keeps the last value for each type.
    /// </summary>
    internal static string ProjectRecapToSummary(string body) {
        string? summaryText = null;
        string? plan        = null;

        try {
            if (JsonNode.Parse(body) is JsonArray root) {
                foreach (var node in root) {
                    var type    = node?["type"]?.GetValue<string>();
                    var content = node?["content"]?.GetValue<string>();

                    if (content is null) continue;

                    if (type == "whats_done") {
                        summaryText = content;
                    } else if (type == "plan") {
                        plan = content;
                    }
                }
            }
        } catch {
            // Malformed body — fall through with empty projection.
        }

        // AOT-safe construction: build a JSON fragment from encoded primitives rather than assigning
        // strings directly to a JsonObject. JsonNode.Parse handles arbitrary string content safely.
        var sb = new StringBuilder();
        sb.Append("{\"summary_text\":");
        AppendJsonString(sb, summaryText ?? "");
        sb.Append(",\"plan\":");

        if (plan is null) {
            sb.Append("null");
        } else {
            AppendJsonString(sb, plan);
        }

        sb.Append('}');

        return sb.ToString();
    }

    static void AppendJsonString(StringBuilder sb, string value) {
        sb.Append('"');
        sb.Append(JsonEncodedText.Encode(value));
        sb.Append('"');
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

    static McpTool[] BuildToolsList() => [
        new(
            "search_sessions",
            "Search past Kurrent Capacitor sessions in the current repo (or across all visible repos with repo: \"all\") by free-text question and/or author name. Returns ranked hits with session_id, title, owner, snippet, and (for transcript hits) hit_event_index + agent_id for drilling into the exact moment with get_session_transcript. Use this for 'why did X happen?', 'who decided Y?', 'when did we work on Z?' questions.",
            new(
                "object",
                new() {
                    ["query"]            = new("string",  "Free-text FTS query. Empty allowed when author is set."),
                    ["author"]           = new("string",  "Optional: GitHub username or display name. Fuzzy match."),
                    ["author_github_id"] = new("integer", "Optional: explicit GitHub numeric id. Takes precedence over `author`."),
                    ["repo"]             = new("string",  "Optional: \"all\" for cross-repo, \"<owner>/<name>\", or a 16-hex repo hash. Defaults to the current repo (resolved from cwd at server startup)."),
                    ["limit"]            = new("integer", "Default 10, max 50."),
                    ["offset"]           = new("integer", "Default 0, max 500.")
                },
                []
            )
        ),
        new(
            "get_session_summary",
            "Get a concise summary of a past session: the 'what was done' narrative (summary_text) and the plan (if any). Use this to orient yourself before drilling into the full transcript.",
            new(
                "object",
                new() { ["session_id"] = new("string", "Session ID returned by search_sessions") },
                ["session_id"]
            )
        ),
        new(
            "get_session_transcript",
            "Get speaker-tagged transcript excerpts from a past session. Use `around_event` (paired with `agent_id` for subagent hits) returned by search_sessions to fetch the exact decision context. Default window is 50 events from the beginning; with `around_event` it's ±5/15 by default.",
            new(
                "object",
                new() {
                    ["session_id"]       = new("string",  "Session ID."),
                    ["around_event"]     = new("integer", "Center the window around this event index."),
                    ["agent_id"]         = new("string",  "When the search hit was in a subagent stream, the agent_id returned alongside hit_event_index."),
                    ["before"]           = new("integer", "Events before around_event. Default 5."),
                    ["after"]            = new("integer", "Events after around_event. Default 15."),
                    ["limit"]            = new("integer", "When around_event is unset. Default 50."),
                    ["offset"]           = new("integer", "When around_event is unset. Default 0."),
                    ["chain"]            = new("boolean", "Include chained_sessions metadata. Default false."),
                    ["include_thinking"] = new("boolean", "Include assistant thinking blocks. Default false.")
                },
                ["session_id"]
            )
        )
    ];
}
