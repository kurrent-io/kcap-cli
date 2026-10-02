using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Commands;

/// <summary>The <c>start_agent</c> tool: one POST asking the server to start a separate hosted agent,
/// answered when the server answers. Nothing here waits for the agent or reads it back.</summary>
static class StartAgentTool {
    internal const string Name  = "start_agent";
    internal const string Route = "/api/agents/start";

    internal const int MaxPromptBytes = 16_384;

    /// <summary>The POST is never re-sent, because a second one would start a second agent. The bound
    /// keeps a server that stops answering from holding the serial tool loop.</summary>
    internal static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    internal const string NoVendorMessage =
        "No vendor: pass vendor as a lowercase token such as 'claude' or 'codex'. The harness running this session does not identify itself to this server.";

    internal const string OutcomeUnknown =
        "The agent may or may not have started. Open the agents page before starting it again.";

    internal const string ServerCannotStartAgents =
        "This server cannot start agents: POST /api/agents/start was answered 404 or 405 with no refusal code, so the server has no such route or has the feature switched off. Nothing was started.";

    internal const string UnreadableAnswer =
        "Error: the server accepted the start (POST /api/agents/start) but its answer could not be read. An agent was most likely started: open the agents page before starting it again.";

    internal const string RequestedNotice =
        "Requested, not confirmed: this call returned when the launch command was sent, before the agent registered. The agent runs on its own from here. Do not wait for it and do not poll; give the user the url.";

    internal static McpTool Describe() => new(
        Name,
        "Start a SEPARATE hosted agent in a git repository on this machine, give it a task, and return at once. " +
        "This is the tool for handing work to another agent that runs on its own: not your harness's built-in subagent or background-agent tool, which runs inside this session and is recorded as part of it, not a flow (start_flow, start_review_flow), and not the `kcap agent` CLI. " +
        "EXPLICIT INTENT: call only after the user asked for separate agents and approved the list of starts. Each call starts one agent, which runs a paid model in its own session and its own worktree. " +
        "Call list_start_agent_options first: it says whether a daemon runs on this machine and which harnesses it can start. When none runs here, do not call this. When the user did not name a harness, ask which one before calling. " +
        "This call does not block and nothing polls: it answers `requested` when the launch command has been sent, before the agent registers, with the agent id and the url of its page. The user supervises the agent from the dashboard. Do not wait for it, and do not call this again to check on it. " +
        "The prompt is all the agent gets. It shares none of this conversation, so write a task that stands alone and name this session's id as its source. " +
        "work_item is required and has no default: name the work item or loose end the task belongs to, `requester` for this session's own work item, or `none`. The server attempts ONE attach of the new session to that item after the session starts; nothing is attached when this call returns. " +
        "A refusal states its reason: no daemon on this machine, the daemon at capacity, the limit of live agents, a start on the same key already in progress. Relay it to the user instead of retrying in a loop.",
        new(
            "object",
            new() {
                ["cwd"]        = new("string", "Absolute path of a directory on this machine, inside the git repository the agent should work in. The agent gets its own worktree of that repository; a path inside a linked worktree resolves to the repository the worktree belongs to."),
                ["prompt"]     = new("string", "The task, self-contained. At most 16384 bytes of UTF-8."),
                ["work_item"]  = new("string", "Required. One of: a `wi:` or `le:` target key exactly as get_next_work printed it; a work item id; `requester` (this session's primary work item); `none`."),
                ["vendor"]     = new("string", "The harness to start, as a lowercase token listed by list_start_agent_options (e.g. 'claude', 'codex'). Pass the one the user chose; ask them when they did not name one. Defaults to the harness running this session, and is required when that harness cannot be identified."),
                ["model"]      = new("string", "Optional. The vendor's own model id or alias: letters, digits and . _ : - / only, at most 64 characters. Omit to let the daemon use the vendor's default."),
                ["daemon"]     = new("string", "Optional. A daemon's name. Needed only after the server answers ambiguous_daemon because several of your daemons run on this machine."),
                ["session_id"] = new("string", "Optional. The calling session. Defaults to the session this server runs in; a value given here wins.")
            },
            ["cwd", "prompt", "work_item"]
        ),
        McpToolAnnotations.Launch
    );

    internal static StartAgentDto BuildRequest(
            JsonObject? arguments, string? ambientSessionId, string? driverVendor, string? callerAgentId, string? machineId) {
        var sessionId = McpSessionId.ResolveWithin(arguments, ambientSessionId);
        var cwd       = McpToolArguments.RequireString(arguments, "cwd").Trim();
        var prompt    = McpToolArguments.RequireString(arguments, "prompt");
        var workItem  = McpToolArguments.RequireString(arguments, "work_item").Trim();
        var vendor    = (McpToolArguments.OptionalString(arguments, "vendor") ?? driverVendor)?.ToLowerInvariant()
                     ?? throw new ArgumentException(NoVendorMessage);

        var promptBytes = Encoding.UTF8.GetByteCount(prompt);

        if (promptBytes > MaxPromptBytes)
            throw new ArgumentException(
                $"'prompt' is {promptBytes} bytes of UTF-8; the limit is {MaxPromptBytes}. Shorten it, or have the agent read the detail from a file.");

        return new(
            sessionId, cwd, RepoPathOf(cwd), prompt, workItem, vendor, machineId, callerAgentId,
            McpToolArguments.OptionalString(arguments, "model"),
            McpToolArguments.OptionalString(arguments, "daemon"));
    }

    static string RepoPathOf(string cwd) {
        if (!Path.IsPathFullyQualified(cwd)) throw new ArgumentException($"'cwd' must be an absolute path: {cwd}");
        if (!Directory.Exists(cwd)) throw new ArgumentException($"'cwd' is not an existing directory: {cwd}");

        var root = GitRepository.FindRoot(cwd) ?? throw new ArgumentException($"'cwd' is not inside a git repository: {cwd}");

        return GitRepository.ResolveMainRepoRoot(root);
    }

    internal static async Task<(HttpResponseMessage? Response, string How)> PostAsync(
            HttpClient client, string apiRoot, StartAgentDto body, FlowRetryClock clock) {
        using var timeout = clock.CreateTimeoutSource(StartTimeout);

        try {
            return (await client.PostAsync(apiRoot + Route, JsonContent.Create(body, McpJsonContext.Default.StartAgentDto), timeout.Token), "");
        } catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException) {
            return (null, ex is OperationCanceledException ? $"timed out after {(int)StartTimeout.TotalSeconds} s" : $"failed: {ex.Message}");
        }
    }

    internal static string Unanswered(string how) => $"Error: the start request (POST {Route}) {how}. {OutcomeUnknown}";

    internal static (string Text, bool IsError) Render(int status, string body) {
        using var document = TryParse(body);
        var root = document?.RootElement ?? default;

        if (status is >= 200 and < 300)
            return FormatRequested(root) is { } requested ? (requested, false) : (UnreadableAnswer, true);

        var code = root.Str("error") is { Length: > 0 } stated ? stated : null;

        if ((status is 404 or 405) && code is null) return (ServerCannotStartAgents, true);

        var text = new StringBuilder();

        if (code is null) {
            text.Append($"Error: HTTP {status} — {body}");
        } else {
            text.Append($"Error ({code})");
            if (root.Str("message") is { Length: > 0 } message) text.Append($": {message}");

            AppendNames(text, root, "daemons");
            AppendNames(text, root, "vendors");
            AppendNumber(text, root, "active");
            AppendNumber(text, root, "max");
            AppendNumber(text, root, "limit");
        }

        if (status >= 500 && code is null) text.Append('\n').Append(OutcomeUnknown);

        return (text.ToString(), true);
    }

    static JsonDocument? TryParse(string body) {
        try {
            return JsonDocument.Parse(body);
        } catch (JsonException) {
            return null;
        }
    }

    static string? FormatRequested(JsonElement root) {
        if (root.Str("agent_id") is not { Length: > 0 } agentId) return null;

        var text = new StringBuilder($"status: requested\nagent_id: {agentId}\n");

        foreach (var field in (string[])["url", "daemon", "repo_path", "vendor", "model"])
            if (root.Str(field) is { Length: > 0 } value) text.Append($"{field}: {value}\n");

        var workItem = root.Obj("work_item") ?? default;
        var reason   = workItem.Str("reason") is { Length: > 0 } given ? given : "unstated";

        text.Append(workItem.Str("id") is { Length: > 0 } id
            ? $"work_item: {id} ({reason}): an attach will be attempted once the agent's session is known; none has happened yet\n"
            : $"work_item: none ({reason})\n");

        return text.Append(RequestedNotice).ToString();
    }

    static void AppendNames(StringBuilder text, JsonElement root, string key) {
        if (root.Arr(key) is not { } array) return;

        var names = array.EnumerateArray().Where(e => e.IsString).Select(e => e.GetString()).OfType<string>().ToList();

        if (names.Count > 0) text.Append($"\n{key}: {string.Join(", ", names)}");
    }

    static void AppendNumber(StringBuilder text, JsonElement root, string key) {
        if (root.Num(key) is { } number) text.Append($"\n{key}: {number}");
    }
}
