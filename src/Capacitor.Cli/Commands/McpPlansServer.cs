using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Plans;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.Cli.Commands;

/// <summary>MCP tools for the plan ledger: declare the plan, spec or design document a session works
/// from, declare and update its task list, and read the plan back. The document is read here, so
/// its hash and snapshot are the CLI's; the server keys it off the path and the workspace root the
/// same way discovery keys a repo file. Same stdio JSON-RPC loop as <see cref="McpWorkItemsServer"/>.</summary>
sealed class McpPlansServer(ConfigRoot config, ProfileContext profiles, TokenStore tokens, ICapacitorHttpClient http,
        TelemetryStartup startup, WorkingDirectory workdir, TimeProvider time) {
    /// <summary>The server's per-artifact transport cap; a larger document is declared by hash only.</summary>
    internal const int MaxSnapshotBytes = 256 * 1024;

    /// <summary>The reserved plan id the server resolves to the session's most recently written plan.</summary>
    internal const string CurrentPlan = "current";

    public async Task<int> RunAsync() {
        var baseUrl = profiles.Resolution.ServerUrl!;

        // Resolved once, from the running harness rather than the inherited environment (see
        // HarnessRequesterContext): a relative document path is resolved against the directory the
        // harness is working in, and the repo root is what the path is keyed against.
        var requester = HarnessRequesterContext.Resolve();
        var cwd       = requester.ProjectDir ?? workdir.Path;
        var repoRoot  = GitRepository.FindRoot(cwd);

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
                return await HandleToolCallAsync(callId, callRequest, client, baseUrl, cwd, repoRoot);
            } catch (Exception ex) {
                // The detail goes to stderr, not the client, which could leak local paths.
                await Console.Error.WriteLineAsync($"kcap mcp plans: unexpected error handling tools/call: {ex}");
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
                mcp.ToolCalled("kcap-plans", tool, ok, CommandTiming.ElapsedMs(start, time));
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
        "Use these tools to keep Capacitor's record of the plan this session executes. When you write or are " +
        "handed a plan, spec or design document, declare it with declare_plan_document. When a plan has " +
        "discrete steps, declare them with set_plan_tasks and record every status change with " +
        "update_plan_task; after context compaction, get_plan returns the list with its ids. Keep whatever " +
        "ledger your own workflow asks for as well — these tools replace the harness's task list, not your notes.";

    static string BuildInitializeResponse(JsonNode id, JsonObject request) =>
        ToResponse<McpInitResult>(
            id,
            new(McpProtocol.NegotiateVersion(request), new(new()), new("kcap-plans", "1.0.0"), ServerInstructions),
            McpJsonContext.Default.McpInitResult
        );

    static string BuildToolsListResponse(JsonNode id, McpTool[] tools) =>
        ToResponse(id, new McpToolsResult(tools), McpJsonContext.Default.McpToolsResult);

    internal async Task<string> HandleToolCallAsync(
            JsonNode   id,
            JsonObject request,
            HttpClient client,
            string     baseUrl,
            string     cwd,
            string?    repoRoot
        ) {
        var paramsNode = request["params"]?.AsObject();
        var toolName   = paramsNode?["name"]?.GetValue<string>();
        var arguments  = paramsNode?["arguments"]?.AsObject();

        if (toolName is null) {
            return BuildErrorResponse(id, -32602, "Missing params.name");
        }

        try {
            return toolName switch {
                PlanToolNames.DeclareDocument => await DeclareAsync(id, client, baseUrl, arguments, cwd, repoRoot),
                PlanToolNames.SetTasks        => await SetTasksAsync(id, client, baseUrl, arguments),
                PlanToolNames.UpdateTask      => await UpdateTaskAsync(id, client, baseUrl, arguments),
                PlanToolNames.GetPlan         => await GetPlanAsync(id, client, baseUrl, arguments),
                _                             => throw new ArgumentException($"Unknown tool: {toolName}")
            };
        } catch (ArgumentException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        } catch (HttpRequestException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        }
    }

    async Task<string> DeclareAsync(JsonNode id, HttpClient client, string baseUrl, JsonObject? args, string cwd, string? repoRoot) {
        var declaration = BuildDeclaration(args, cwd, repoRoot);

        using var response = await client.PostAsync($"{baseUrl}/api/plans/documents", ToJsonContent(declaration.Body));

        return await RelayAsync(id, response, baseUrl, body => AnnotateDeclaration(body, declaration));
    }

    async Task<string> SetTasksAsync(JsonNode id, HttpClient client, string baseUrl, JsonObject? args) {
        var body   = BuildSetTasksBody(args);
        var planId = OptionalPlanId(args) ?? CurrentPlan;

        using var response = await client.PostAsync($"{baseUrl}/api/plans/{Uri.EscapeDataString(planId)}/tasks", ToJsonContent(body));

        return await RelayAsync(id, response, baseUrl);
    }

    /// <summary>The update route answers with the task alone, so the plan is resolved first when the
    /// caller named none: the result must say which plan it acted on, and a session with no plan
    /// gets an error that names the fix rather than a bare 404.</summary>
    async Task<string> UpdateTaskAsync(JsonNode id, HttpClient client, string baseUrl, JsonObject? args) {
        var sessionId = McpSessionId.Resolve(args);
        var taskRef   = TaskRef(args);
        var body      = BuildUpdateBody(args, sessionId);
        var planId    = OptionalPlanId(args);

        if (planId is null) {
            using var current = await client.GetAsync(CurrentPlanUrl(baseUrl, sessionId));

            if (current.StatusCode == HttpStatusCode.NotFound)
                return BuildToolResult(id, NoPlanMessage(sessionId), isError: true);

            if (!current.IsSuccessStatusCode) return await RelayAsync(id, current, baseUrl);

            planId = ReadPlanId(await current.Content.ReadAsStringAsync())
                  ?? throw new ArgumentException("The server's current-plan response carries no plan_id.");
        }

        using var response = await client.PostAsync(
            $"{baseUrl}/api/plans/{Uri.EscapeDataString(planId)}/tasks/{Uri.EscapeDataString(taskRef)}", ToJsonContent(body));

        return await RelayAsync(id, response, baseUrl, task => new JsonObject {
            ["plan_id"] = planId,
            ["task"]    = TryParseObject(task) ?? (JsonNode)JsonValue.Create(task)!
        }.ToJsonString());
    }

    async Task<string> GetPlanAsync(JsonNode id, HttpClient client, string baseUrl, JsonObject? args) {
        if (OptionalPlanId(args) is { } planId) {
            using var response = await client.GetAsync($"{baseUrl}/api/plans/{Uri.EscapeDataString(planId)}");

            return await RelayAsync(id, response, baseUrl);
        }

        var sessionId = McpSessionId.Resolve(args);

        using var current = await client.GetAsync(CurrentPlanUrl(baseUrl, sessionId));

        // A session on no plan is a valid answer, not a failure: the agent learns it has nothing
        // to recover and declares afresh.
        if (current.StatusCode == HttpStatusCode.NotFound)
            return BuildToolResult(id, EmptyPlan(sessionId));

        return await RelayAsync(id, current, baseUrl);
    }

    async Task<string> RelayAsync(JsonNode id, HttpResponseMessage response, string baseUrl, Func<string, string>? shape = null) {
        var body = await response.Content.ReadAsStringAsync();

        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return BuildToolResult(id, await AuthRejectionNotice.ForPersistentUnauthorizedAsync(tokens, profiles.Name, baseUrl, time), isError: true);

        if (!response.IsSuccessStatusCode)
            return BuildToolResult(id, $"Error: HTTP {(int)response.StatusCode} — {body}", isError: true);

        return BuildToolResult(id, shape is null ? body : shape(body));
    }

    internal static string CurrentPlanUrl(string baseUrl, string sessionId) =>
        $"{baseUrl}/api/plans/{CurrentPlan}?session_id={Uri.EscapeDataString(sessionId)}";

    internal static string NoPlanMessage(string sessionId) =>
        $"Error: session {sessionId} has no plan yet — declare a plan document with declare_plan_document or call set_plan_tasks first.";

    internal static string EmptyPlan(string sessionId) =>
        new JsonObject {
            ["plan_id"]    = null,
            ["session_id"] = sessionId,
            ["documents"]  = new JsonArray(),
            ["tasks"]      = new JsonArray(),
            ["progress"]   = new JsonObject { ["completed"] = 0, ["total"] = 0, ["total_known"] = false },
            ["message"]    = "No plan declared for this session yet."
        }.ToJsonString();

    static string AnnotateDeclaration(string body, PlanDocumentDeclaration declaration) {
        if (TryParseObject(body) is not { } result) return body;

        result["path"]              = declaration.WirePath;
        result["workspace_root"]    = declaration.WorkspaceRoot;
        result["content_bytes"]     = declaration.ContentBytes;
        result["snapshot_attached"] = declaration.SnapshotAttached;
        if (declaration.SnapshotOmitted is { } reason)
            result["message"] = $"{reason}: declared by hash only.";

        return result.ToJsonString();
    }

    static JsonObject? TryParseObject(string text) {
        try {
            return JsonNode.Parse(text) as JsonObject;
        } catch {
            return null;
        }
    }

    internal static string? ReadPlanId(string body) =>
        TryParseObject(body)?["plan_id"] is JsonValue v && v.TryGetValue<string>(out var id) && !string.IsNullOrWhiteSpace(id) ? id : null;

    static StringContent ToJsonContent(JsonObject body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    // Content is declared verbatim, so a snapshot must hash to what the server stores: a file that
    // is not valid UTF-8 goes by hash alone rather than as a replacement-character rendering.
    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal static PlanDocumentDeclaration BuildDeclaration(JsonObject? args, string cwd, string? repoRoot) {
        var sessionId = McpSessionId.Resolve(args);
        var kind      = McpToolArguments.RequireString(args, "kind");
        var rawPath   = McpToolArguments.RequireString(args, "path");
        var boundary  = repoRoot ?? cwd;
        var fullPath  = ContainedPath(rawPath, cwd, boundary);

        if (!File.Exists(fullPath))
            throw new ArgumentException($"'{rawPath}' does not exist (resolved against {cwd}).");

        var (hash, snapshot, length) = ReadDocument(fullPath);
        var wirePath = WirePath(fullPath, repoRoot);

        var body = new JsonObject {
            ["session_id"]     = sessionId,
            ["kind"]           = kind,
            ["path"]           = wirePath,
            ["workspace_root"] = repoRoot,
            ["content_hash"]   = hash
        };

        string? omitted = null;

        if (snapshot is null) {
            omitted = $"Content exceeds {MaxSnapshotBytes} bytes";
        } else {
            try {
                body["content"] = StrictUtf8.GetString(snapshot);
            } catch (DecoderFallbackException) {
                omitted = "Content is not valid UTF-8";
            }
        }

        if (McpToolArguments.OptionalString(args, "argues_from") is { } arguesFrom)
            body["argues_from"] = WirePath(Path.GetFullPath(arguesFrom, cwd), repoRoot);

        if (McpToolArguments.OptionalString(args, "work_item_id") is { } workItemId)
            body["work_item_id"] = workItemId;

        return new(body, wirePath, repoRoot, length, omitted);
    }

    /// <summary>Bound on link hops while resolving a declared path; a chain deeper than this is a
    /// cycle or an attack, either way refused.</summary>
    const int MaxLinkHops = 32;

    /// <summary>Resolves the declared path and refuses one that leaves the project — by an absolute
    /// path, a <c>..</c> segment, or a symlink anywhere below the boundary — before a byte of it is
    /// read: the server rejects such a path too, but only after the content has reached it.</summary>
    internal static string ContainedPath(string rawPath, string cwd, string boundary) {
        var fullPath = Path.GetFullPath(rawPath, cwd);

        if (!IsInside(fullPath, boundary))
            throw new ArgumentException($"'{rawPath}' is outside the project root ({boundary}); only files under it can be declared.");

        // Both sides are resolved the way the kernel opens them, so a link above the root (macOS's
        // /var → /private/var) cancels out and only a link that leaves the tree remains.
        if (!IsInside(ResolveLinks(fullPath, rawPath), ResolveLinks(boundary, rawPath)))
            throw new ArgumentException($"'{rawPath}' links outside the project root ({boundary}); only files under it can be declared.");

        return fullPath;
    }

    /// <summary>The path with every link resolved, walked as the kernel opens it: one raw component
    /// at a time from the filesystem root, <c>..</c> applied to the canonical directory reached so
    /// far, and a link's raw target spliced in unnormalized — so a <c>..</c> inside a target climbs
    /// out of whatever the link before it resolved to, never out of a lexical stand-in.</summary>
    static string ResolveLinks(string absolutePath, string rawPath) {
        var current = Path.GetPathRoot(absolutePath)!;
        var pending = new Queue<string>(RawComponents(absolutePath[current.Length..]));
        var hops    = 0;

        while (pending.Count > 0) {
            var component = pending.Dequeue();

            if (component == ".") continue;

            if (component == "..") {
                current = Path.GetDirectoryName(current) ?? current;
                continue;
            }

            var candidate = Path.Combine(current, component);
            FileSystemInfo node = Directory.Exists(candidate) ? new DirectoryInfo(candidate) : new FileInfo(candidate);

            if (node.LinkTarget is not { } target) {
                current = candidate;
                continue;
            }

            if (++hops > MaxLinkHops)
                throw new ArgumentException($"'{rawPath}' links too deeply to resolve.");

            if (Path.IsPathFullyQualified(target)) {
                current = Path.GetPathRoot(target)!;
                target  = target[current.Length..];
            } else if (Path.IsPathRooted(target)) {
                // Windows only: `\x\y` stays on the volume the link sits on, and `D:x` (relative to
                // another drive's current directory) has no resolution worth trusting.
                if (Path.GetPathRoot(target)!.Length > 1)
                    throw new ArgumentException($"'{rawPath}' links through a drive-relative target, which cannot be resolved safely.");

                current = Path.GetPathRoot(current)!;
            }

            pending = new Queue<string>(RawComponents(target).Concat(pending));
        }

        return current;
    }

    static IEnumerable<string> RawComponents(string path) =>
        path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

    static bool IsInside(string path, string root) {
        var relative = Path.GetRelativePath(root, path);

        return relative != ".." && !Path.IsPathRooted(relative)
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith("../", StringComparison.Ordinal);
    }

    /// <summary>Root-relative with forward slashes when the file is inside the repo, so the server
    /// keys it exactly as discovery keys the same file; the absolute path otherwise, which the
    /// server accepts only when its own captured root contains it.</summary>
    internal static string WirePath(string fullPath, string? repoRoot) {
        if (repoRoot is null || !IsInside(fullPath, repoRoot)) return fullPath;

        var relative = Path.GetRelativePath(repoRoot, fullPath);

        return relative == "." ? fullPath : relative.Replace('\\', '/');
    }

    /// <summary>The hash of the whole file and, only when it fits the cap, its bytes: a large file
    /// is hashed from the stream so the long-lived server never holds more than the cap. Shared-read
    /// so the agent that just wrote the document is never denied its own write handle.</summary>
    static (string Hash, byte[]? Snapshot, long Length) ReadDocument(string path) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var length = stream.Length;

        if (length > MaxSnapshotBytes)
            return (Convert.ToHexStringLower(SHA256.HashData(stream)), null, length);

        var bytes = new byte[length];
        stream.ReadExactly(bytes);

        return (Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes, length);
    }

    internal static JsonObject BuildSetTasksBody(JsonObject? args) {
        var body = new JsonObject { ["session_id"] = McpSessionId.Resolve(args) };

        if (args is null || !args.TryGetPropertyValue("tasks", out var node) || node is null)
            throw new ArgumentException("'tasks' is required.");

        if (node is not JsonArray array) throw new ArgumentException("'tasks' must be an array of task objects.");

        var tasks = new JsonArray();

        foreach (var element in array) {
            if (element is not JsonObject task)
                throw new ArgumentException("'tasks' must contain only objects, each with a 'title'.");

            var entry = new JsonObject { ["title"] = McpToolArguments.RequireString(task, "title") };

            if (McpToolArguments.OptionalString(task, "task_id") is { } taskId) entry["task_id"] = taskId;
            if (McpToolArguments.OptionalString(task, "status") is { } status) entry["status"] = status;
            if (McpToolArguments.OptionalString(task, "note") is { } note) entry["note"] = note;

            // Cast to JsonNode so the non-generic Add(JsonNode?) overload is chosen —
            // the generic Add<T>(T) trips IL2026/IL3050 under AOT (see CLAUDE.md).
            tasks.Add((JsonNode?)entry);
        }

        body["tasks"] = tasks;

        return body;
    }

    internal static JsonObject BuildUpdateBody(JsonObject? args, string sessionId) {
        var body = new JsonObject {
            ["session_id"] = sessionId,
            ["status"]     = McpToolArguments.RequireString(args, "status")
        };

        if (McpToolArguments.OptionalString(args, "note") is { } note) body["note"] = note;

        return body;
    }

    /// <summary>The route segment naming the task: its id, or its 1-based ordinal.</summary>
    internal static string TaskRef(JsonObject? args) {
        var taskId     = McpToolArguments.OptionalString(args, "task_id");
        var hasOrdinal = McpToolArguments.TryReadInt(args, "ordinal", out var ordinal);

        if (taskId is not null && hasOrdinal) throw new ArgumentException("Pass either 'task_id' or 'ordinal', not both.");
        if (taskId is not null) return ValidId(taskId, "task_id");
        if (hasOrdinal) return ordinal > 0 ? ordinal.ToString(CultureInfo.InvariantCulture) : throw new ArgumentException("'ordinal' must be a positive integer.");

        throw new ArgumentException("Pass 'task_id' or 'ordinal' to name the task.");
    }

    internal static string? OptionalPlanId(JsonObject? args) =>
        McpToolArguments.OptionalString(args, "plan_id") is { } id ? ValidId(id, "plan_id") : null;

    // `.` is unreserved, so escaping leaves a dot segment intact and URI normalization would walk
    // it out of the route.
    static string ValidId(string id, string key) =>
        id is "." or ".." ? throw new ArgumentException($"'{key}' is not a valid id.") : id;

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
        new(PlanToolNames.DeclareDocument,
            "Declare the plan, spec or design document this session works from. Call it when you write such a "
          + "document or are handed one. The file is read locally; its SHA-256 and, up to 256 KB, its content go "
          + "to the Capacitor server this session is recorded to — the one `kcap login` signed in to, which "
          + "already holds the session's transcript — keyed by path against the git repository root. Nothing is "
          + "published anywhere else. Returns plan_id, document_key and whether the plan was created; declaring "
          + "the same document again attaches this session to the same plan.",
            new("object", new() {
                ["kind"]         = new("string", "One of 'plan', 'spec' or 'design'."),
                ["path"]         = new("string", "Path to the document, absolute or relative to the project directory."),
                ["argues_from"]  = new("string", "Path of the document this one argues from — the spec a plan implements, or the design a spec refines — so both land on one plan."),
                ["work_item_id"] = new("string", "Work item the plan belongs to, when known."),
                ["session_id"]   = new("string", "Session to attach. Defaults to the session this server runs in when omitted.")
            }, ["kind", "path"]), McpToolAnnotations.Upsert),
        new(PlanToolNames.SetTasks,
            "Declare the plan's task list as a full ordered snapshot, replacing the declared list. Call it when a "
          + "plan has discrete steps, and again — with the whole list — when the steps change. An entry carrying a "
          + "task_id the plan already knows keeps it; the rest are minted. Without plan_id the session's current "
          + "plan is used, and a session with no plan gets one created. Returns the tasks with their ids and ordinals.",
            new("object", new() {
                ["tasks"]      = new("array", "The complete ordered task list.",
                    new("object", "A task: {title, task_id?, status?: pending|in_progress|completed|skipped, note?}.")),
                ["plan_id"]    = new("string", "Plan to write to. Defaults to the session's current plan."),
                ["session_id"] = new("string", "Session making the declaration. Defaults to the session this server runs in when omitted.")
            }, ["tasks"]), McpToolAnnotations.Destructive),
        new(PlanToolNames.UpdateTask,
            "Record one task's status transition — call it every time a task starts, finishes or is skipped. Name "
          + "the task by task_id (from set_plan_tasks or get_plan) or by its 1-based ordinal. Without plan_id the "
          + "session's current plan is used. The result names the plan it acted on.",
            new("object", new() {
                ["task_id"]    = new("string", "The task's id."),
                ["ordinal"]    = new("integer", "The task's 1-based position, as an alternative to task_id."),
                ["status"]     = new("string", "One of 'pending', 'in_progress', 'completed' or 'skipped'."),
                ["note"]       = new("string", "Optional note on the transition — why a task was skipped, what blocked it."),
                ["plan_id"]    = new("string", "Plan the task belongs to. Defaults to the session's current plan."),
                ["session_id"] = new("string", "Session recording the change. Defaults to the session this server runs in when omitted.")
            }, ["status"]), McpToolAnnotations.Upsert),
        new(PlanToolNames.GetPlan,
            "Read a plan back: its documents, tasks with status and source, and progress. Call it to recover the "
          + "task list after context compaction instead of re-reading a ledger file. Without plan_id the session's "
          + "current plan is returned; a session with no plan gets an empty result, not an error.",
            new("object", new() {
                ["plan_id"]    = new("string", "Plan to read. Defaults to the session's current plan."),
                ["session_id"] = new("string", "Session whose current plan to read. Defaults to the session this server runs in when omitted.")
            }, []), McpToolAnnotations.Read)
    ];
}
