using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Telemetry;
using Capacitor.Cli.Core.Config;

using Capacitor.Cli.Core.Http;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Commands;

sealed class McpMemoryServer(ConfigRoot config, ProfileContext profiles, TokenStore tokens, ICapacitorHttpClient http,
        TelemetryStartup startup, GitProviderRouter router, WorkingDirectory workdir, TimeProvider time) {
    internal const string NotLoggedInMessage = AuthRejectionNotice.NotLoggedIn;

    public async Task<int> RunAsync() {
        var baseUrl = profiles.Resolution.ServerUrl!;

        var repository = new CwdRepository(config, workdir.Path, router, time);
        var machineId  = await ResolveMachineIdAsync();
        var tools      = BuildToolsList();

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

        // Validate the server_url shape once, locally (pure string check — no network, token,
        // or stderr). Used to fail gracefully instead of hard-exiting mid-request (below).
        var urlOk = HttpClientExtensions.IsAcceptableUrl(baseUrl);

        // Created on first use, so a session that never calls a tool stays local-only.
        using var sessionClient = new OnDemand<HttpClient>(() => http.ForSessionAsync());

        // Guarded tool dispatch: never let the stdio JSON-RPC loop die on one bad request. An
        // unexpected failure would otherwise bubble out of the loop and kill the server mid-protocol;
        // return a JSON-RPC tool error instead so it keeps serving.
        async Task<string> DispatchToolCallAsync(JsonNode callId, JsonObject callRequest) {
            if (!urlOk)
                return BuildToolResult(callId, HttpClientExtensions.SchemeMissingHint, isError: true);

            try {
                var client = await sessionClient.GetAsync();
                return await HandleToolCallAsync(callId, callRequest, client, baseUrl, await repository.GetHashAsync(), machineId);
            } catch (Exception ex) {
                // Unexpected: log the detail to stderr (not to the client, which could leak local
                // paths from IO errors) and return a generic tool error, keeping the loop alive.
                await Console.Error.WriteLineAsync($"kcap mcp memory: unexpected error handling tools/call: {ex}");
                return BuildToolResult(callId, "Error: internal error handling the request.", isError: true);
            }
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
                mcp.ToolCalled("kcap-memory", tool, ok, CommandTiming.ElapsedMs(start, time));
            }
        }

        await using var stdin  = Console.OpenStandardInput();
        await using var stdout = Console.OpenStandardOutput();
        using var       reader = new StreamReader(stdin, Encoding.UTF8);
        await using var writer = new StreamWriter(stdout, new UTF8Encoding(false));
        writer.AutoFlush = true;

        await McpStdioLoop.RunAsync(reader, writer, tools, TimedDispatchToolCallAsync, (id, method, request) => method switch {
            "initialize" => BuildInitializeResponse(id, request),
            "tools/list" => BuildToolsListResponse(id, tools),
            _            => McpProtocol.TryHandleStandardMethod(method, id)
                            ?? BuildErrorResponse(id, -32601, $"Method not found: {method}")
        }, "memory");

        return 0;
    }

    async Task<string?> ResolveMachineIdAsync() {
        try { return await MachineIdProvider.GetOrCreateAsync(config); } catch { return null; }
    }

    // Server-level usage preamble (MCP `instructions`) — steers clients to check for prior art here
    // before assuming none, and to save durable learnings.
    internal const string ServerInstructions =
        "Use these tools for durable team knowledge — preferences, feedback, project facts, references. " +
        "Search memories before assuming there's no prior art on a task, and before saving a new one — they " +
        "hold learnings that aren't in the code.";

    static string BuildInitializeResponse(JsonNode id, JsonObject request) =>
        ToResponse<McpInitResult>(
            id,
            new(McpProtocol.NegotiateVersion(request), new(new()), new("kcap-memory", "1.0.0"), ServerInstructions),
            McpJsonContext.Default.McpInitResult
        );

    static string BuildToolsListResponse(JsonNode id, McpTool[] tools) =>
        ToResponse(id, new McpToolsResult(tools), McpJsonContext.Default.McpToolsResult);

    internal async Task<string> HandleToolCallAsync(
            JsonNode   id,
            JsonObject request,
            HttpClient client,
            string     baseUrl,
            string?    cwdRepoHash,
            string?    machineId
        ) {
        var paramsNode = request["params"]?.AsObject();
        var toolName   = paramsNode?["name"]?.GetValue<string>();
        var arguments  = paramsNode?["arguments"]?.AsObject();

        if (toolName is null) {
            return BuildErrorResponse(id, -32602, "Missing params.name");
        }

        try {
            using var httpResponse = toolName switch {
                "search_memories"     => await client.GetAsync(BuildSearchUrl(baseUrl, arguments, cwdRepoHash, machineId)),
                "get_memory"          => await client.GetAsync(BuildGetUrl(baseUrl, arguments, cwdRepoHash, machineId)),
                "save_memory"         => await client.PostAsync($"{baseUrl}/api/memories", ToJsonContent(BuildSaveBody(arguments, cwdRepoHash, machineId))),
                "update_memory"       => await client.PutAsync($"{baseUrl}/api/memories/{Uri.EscapeDataString(Id(arguments))}", ToJsonContent(BuildUpdateBody(arguments))),
                "rescope_memory"      => await client.PostAsync($"{baseUrl}/api/memories/{Uri.EscapeDataString(Id(arguments))}/rescope", ToJsonContent(BuildRescopeBody(arguments))),
                "archive_memory"      => await client.DeleteAsync($"{baseUrl}/api/memories/{Uri.EscapeDataString(Id(arguments))}"),
                "append_memory_entry" => await client.PostAsync(BuildAppendUrl(baseUrl, arguments, cwdRepoHash, machineId), ToJsonContent(BuildAppendBody(arguments))),
                "settle_memory_entry" => await client.PostAsync($"{EntryUrl(baseUrl, arguments)}/settle", ToJsonContent(BuildSettleBody(arguments))),
                "reopen_memory_entry" => await client.PostAsync($"{EntryUrl(baseUrl, arguments)}/reopen", null),
                "list_memory_entries" => await client.GetAsync(BuildListEntriesUrl(baseUrl, arguments)),
                _                     => throw new ArgumentException($"Unknown tool: {toolName}")
            };

            var body = await httpResponse.Content.ReadAsStringAsync();

            if (httpResponse.StatusCode == HttpStatusCode.Unauthorized) {
                return BuildToolResult(id, await AuthRejectionNotice.ForPersistentUnauthorizedAsync(tokens, profiles.Name, baseUrl, time), isError: true);
            }

            if (EntryTools.Contains(toolName) && IsServerWithoutLogs(httpResponse.StatusCode, body)) {
                return BuildToolResult(id, LogsUnsupportedMessage, isError: true);
            }

            if (!httpResponse.IsSuccessStatusCode) {
                return BuildToolResult(id, $"Error: HTTP {(int)httpResponse.StatusCode} — {body}", isError: true);
            }

            return BuildToolResult(id, toolName switch {
                "update_memory" => WithUncheckedVersionNotice(arguments, body),
                "save_memory"   => WithLogFormNotice(arguments, body),
                _               => body
            });
        } catch (ArgumentException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        } catch (HttpRequestException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        }
    }

    /// <summary>A server that predates expected_version ignores it and applies the update unchecked; its answer carries
    /// no version, which is the only sign the caller gets, so the result says so rather than reading as a checked write.</summary>
    internal static string WithUncheckedVersionNotice(JsonObject? args, string body) {
        if (args?["expected_version"] is null) return body;

        try {
            if (JsonNode.Parse(body) is JsonObject { } answer && answer["version"] is not null) return body;
        } catch (JsonException) { }

        return $"{body}\nNote: this server does not support expected_version, so the update was applied without checking for concurrent changes.";
    }

    /// <summary>A server that predates logs ignores <c>form</c> and saves an ordinary memory; its answer names no form.</summary>
    internal static string WithLogFormNotice(JsonObject? args, string body) {
        if (args?["form"] is not JsonValue form || !form.TryGetValue<string>(out var f) || f != "log") return body;

        try {
            if (JsonNode.Parse(body) is JsonObject { } answer && answer["memory"]?["form"] is not null) return body;
        } catch (JsonException) { }

        return $"{body}\nNote: this server does not support log memories, so it treats form: \"log\" as an ordinary memory and append_memory_entry will not work on it.";
    }

    static readonly HashSet<string> EntryTools =
        new(StringComparer.Ordinal) { "append_memory_entry", "settle_memory_entry", "reopen_memory_entry", "list_memory_entries" };

    const string LogsUnsupportedMessage =
        "Error: this server does not support log memories — the entry tools need a newer kcap server.";

    /// <summary>A server without the entry routes answers a POST to one with 405, from its GET-only page fallback, and
    /// a GET with a 404 carrying no <c>detail</c>. The entry routes never answer 405, and every 404 they answer names
    /// what was not found in <c>detail</c>.</summary>
    static bool IsServerWithoutLogs(HttpStatusCode status, string body) =>
        status == HttpStatusCode.MethodNotAllowed || (status == HttpStatusCode.NotFound && !NamesWhatWasNotFound(body));

    static bool NamesWhatWasNotFound(string body) {
        try {
            return JsonNode.Parse(body) is JsonObject answer
                && answer["detail"] is JsonValue detail && detail.TryGetValue<string>(out var text) && text.Length > 0;
        } catch (JsonException) {
            return false;
        }
    }

    static StringContent ToJsonContent(JsonObject body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    static string Id(JsonObject? args) => Required(args, "id");

    static string Required(JsonObject? args, string name) =>
        Optional(args, name) is { } v && !string.IsNullOrWhiteSpace(v) ? v : throw new ArgumentException($"{name} is required");

    static string? Optional(JsonObject? args, string name) =>
        args?[name] switch {
            null                                               => null,
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            _                                                  => throw new ArgumentException($"'{name}' must be a string.")
        };

    static string EntryUrl(string baseUrl, JsonObject? args) =>
        $"{baseUrl}/api/memories/{Uri.EscapeDataString(Id(args))}/entries/{Uri.EscapeDataString(Required(args, "entry_id"))}";

    internal static string BuildSearchUrl(string baseUrl, JsonObject? args, string? cwdRepoHash, string? machineId) {
        var q = Optional(args, "query");
        if (string.IsNullOrWhiteSpace(q)) throw new ArgumentException("query is required");

        var qs = new List<string> { $"q={Uri.EscapeDataString(q)}" };
        if (cwdRepoHash is not null) qs.Add($"repo={Uri.EscapeDataString(cwdRepoHash)}");
        if (machineId is not null) qs.Add($"machine={Uri.EscapeDataString(machineId)}");
        if (OptionalInt(args, "limit") is { } limit) qs.Add($"limit={limit}");

        return $"{baseUrl}/api/memories/search?{string.Join("&", qs)}";
    }

    internal static string BuildGetUrl(string baseUrl, JsonObject? args, string? cwdRepoHash, string? machineId) =>
        ResolvedMemoryUrl(baseUrl, args, "", cwdRepoHash, machineId);

    static string BuildAppendUrl(string baseUrl, JsonObject? args, string? cwdRepoHash, string? machineId) =>
        ResolvedMemoryUrl(baseUrl, args, "/entries", cwdRepoHash, machineId);

    // A slug resolves against the caller's repo and machine, so a route that takes one carries both.
    static string ResolvedMemoryUrl(string baseUrl, JsonObject? args, string suffix, string? cwdRepoHash, string? machineId) {
        var idOrSlug = Required(args, "id_or_slug");

        var qs = new List<string>();
        if (cwdRepoHash is not null) qs.Add($"repo={Uri.EscapeDataString(cwdRepoHash)}");
        if (machineId is not null) qs.Add($"machine={Uri.EscapeDataString(machineId)}");

        var url = $"{baseUrl}/api/memories/{Uri.EscapeDataString(idOrSlug)}{suffix}";
        return qs.Count == 0 ? url : $"{url}?{string.Join("&", qs)}";
    }

    static string BuildListEntriesUrl(string baseUrl, JsonObject? args) {
        var qs = new List<string>();
        if (Optional(args, "status") is { Length: > 0 } status) qs.Add($"status={Uri.EscapeDataString(status)}");
        if (Optional(args, "before") is { Length: > 0 } before) qs.Add($"before={Uri.EscapeDataString(before)}");
        if (OptionalInt(args, "limit") is { } limit) qs.Add($"limit={limit}");

        var url = $"{baseUrl}/api/memories/{Uri.EscapeDataString(Id(args))}/entries";
        return qs.Count == 0 ? url : $"{url}?{string.Join("&", qs)}";
    }

    static int? OptionalInt(JsonObject? args, string name) {
        if (args?[name] is null) return null;

        try {
            if (TryReadInt(args, name, out var value)) return value;
        } catch (ArgumentException) { }

        throw new ArgumentException($"'{name}' must be an integer.");
    }

    static JsonObject BuildAppendBody(JsonObject? args) => new() {
        ["title"]             = Required(args, "title"),
        ["body"]              = Required(args, "body"),
        ["idempotency_key"]   = Optional(args, "idempotency_key"),
        ["source_session_id"] = null,
        ["harness"]           = "mcp"
    };

    static JsonObject BuildSettleBody(JsonObject? args) => new() {
        ["resolution"] = Optional(args, "resolution")
    };

    // NOTE: request bodies use snake_case keys (repo_hash, machine_tag, source_session_id) —
    // the server's global JSON policy is JsonNamingPolicy.SnakeCaseLower.
    // Responses are passed through as raw text, so only these request-body builders are affected.
    internal static JsonObject BuildSaveBody(JsonObject? args, string? cwdRepoHash, string? machineId) {
        string Req(string name) =>
            Optional(args, name) is { Length: > 0 } v ? v : throw new ArgumentException($"{name} is required");

        var global          = ReadBool(args, "global");
        var machineSpecific = ReadBool(args, "machine_specific");
        var project         = Optional(args, "project");

        // A present-but-blank slug is malformed, not omitted: falling back to the repo or org home
        // would silently widen where the memory surfaces.
        if (project is not null && string.IsNullOrWhiteSpace(project))
            throw new ArgumentException("project must be a project slug when supplied.");

        // Fail closed rather than silently broadening scope: the server reads repo_hash: null as an
        // org-wide memory, so a null cwdRepoHash needs an explicit home (project or global). Likewise a
        // null machineId with machine_specific: true would save an untagged (visible-to-everyone) memory.
        if (project is null && !global && cwdRepoHash is null)
            throw new ArgumentException("Cannot resolve the current repository — run from a git checkout, or pass project: \"<slug>\" for a project-scoped memory or global: true for a repo-independent one.");

        if (machineSpecific && machineId is null)
            throw new ArgumentException("Machine id unavailable — cannot save a machine-specific memory on this host.");

        return new JsonObject {
            ["audience"]          = Req("audience"),
            ["slug"]              = Req("slug"),
            ["description"]       = Req("description"),
            ["content"]           = Req("content"),
            ["kind"]              = Req("kind"),
            ["team"]              = Optional(args, "team"),
            // audience 'project' target — the people axis, distinct from the place-axis 'project' below.
            ["audience_project"]  = Optional(args, "audience_project"),
            // The place axis: a project home wins over the repo. The repo hash still rides along because a
            // server that does not know project homes ignores the field and reads a null hash as org-wide;
            // with the hash it lands the memory at the repo, narrower than asked rather than wider.
            ["project"]           = project,
            ["repo_hash"]         = global ? null : cwdRepoHash,
            ["machine_tag"]       = machineSpecific ? machineId : null,
            ["machine_context"]   = machineId,
            ["source_session_id"] = null,
            ["harness"]           = "mcp",
            ["form"]              = Optional(args, "form")
        };
    }

    internal static JsonObject BuildUpdateBody(JsonObject? args) => new() {
        ["description"]      = Optional(args, "description"),
        ["content"]          = Optional(args, "content"),
        ["kind"]             = Optional(args, "kind"),
        ["expected_version"] = Optional(args, "expected_version"),
        ["header"]           = Optional(args, "header")
    };

    internal static JsonObject BuildRescopeBody(JsonObject? args) {
        var audience = Optional(args, "audience");
        var project  = Optional(args, "project");
        var repoHash = Optional(args, "repo_hash");
        var global   = ReadBool(args, "global");

        if (repoHash is not null && string.IsNullOrWhiteSpace(repoHash))
            throw new ArgumentException("repo_hash must be a repo hash when supplied.");

        var placeTargets = (string.IsNullOrWhiteSpace(project) ? 0 : 1) + (repoHash is null ? 0 : 1) + (global ? 1 : 0);
        if (placeTargets > 1)
            throw new ArgumentException("Pass at most one of project, repo_hash or global: true.");

        if (repoHash is not null || global) {
            // A server that does not know repo_hash/global drops them and would apply the audience
            // fields alone — a place move turned into a publish. Sent by itself, it gets a 400 instead.
            string[] audienceFields = ["audience", "team", "audience_project"];
            if (audienceFields.Any(f => !string.IsNullOrWhiteSpace(Optional(args, f))))
                throw new ArgumentException("repo_hash and global: true must be sent alone — change the audience in a separate rescope_memory call.");

            return repoHash is not null ? new() { ["repo_hash"] = repoHash } : new() { ["global"] = true };
        }

        // Mirror the server: a project context move takes precedence and is audience-independent, so
        // audience is required ONLY when project is absent. Whitespace-only counts as absent — a blank
        // slug would never resolve server-side, so reject it locally instead of sending an invalid move.
        if (string.IsNullOrWhiteSpace(audience) && string.IsNullOrWhiteSpace(project))
            throw new ArgumentException("audience, project, repo_hash or global: true is required");
        return new() {
            ["audience"] = string.IsNullOrWhiteSpace(audience) ? null : audience,
            ["team"]     = Optional(args, "team"),
            ["project"]  = string.IsNullOrWhiteSpace(project) ? null : project,
            // audience 'project' target (people axis) — distinct from the place-axis 'project' above; raw
            // like team, not whitespace-nulled, since unlike project it has no precedence branch to guard.
            ["audience_project"] = Optional(args, "audience_project"),
        };
    }

    static bool ReadBool(JsonObject? args, string key) =>
        args?[key] switch {
            null                                            => false,
            JsonValue v when v.TryGetValue<bool>(out var b) => b,
            _                                               => throw new ArgumentException($"'{key}' must be a boolean.")
        };

    /// <summary>
    /// Reads a numeric field as int, tolerant of JsonValue holding any underlying numeric type
    /// (int/long/double) — TryGetValue&lt;int&gt; on a JsonValue constructed from a long returns false.
    /// Returns false when the key is missing or the value is the wrong shape (e.g., a string).
    /// Throws <see cref="ArgumentException"/> when the value is numeric but out of range for int,
    /// so the caller (via <see cref="HandleToolCallAsync"/>) surfaces it as a JSON-RPC validation error
    /// rather than silently falling back to the default.
    /// </summary>
    internal static bool TryReadInt(JsonObject? args, string key, out int value) {
        value = 0;
        var node = args?[key];

        if (node is null) return false;

        JsonValue v;

        try {
            v = node.AsValue();
        } catch {
            return false; // wrong shape (object/array)
        }

        if (v.TryGetValue(out value)) return true;

        if (v.TryGetValue<long>(out var lv)) {
            if (lv is < int.MinValue or > int.MaxValue)
                throw new ArgumentException($"'{key}' value {lv} is out of range for int.");

            value = (int)lv;

            return true;
        }

        if (v.TryGetValue<double>(out var dv)) {
            var rounded = (long)dv;

            if (rounded < int.MinValue || rounded > int.MaxValue || rounded != dv)
                throw new ArgumentException($"'{key}' value {dv} is out of range or non-integer for int.");

            value = (int)rounded;

            return true;
        }

        return false;
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
        new("search_memories",
            "Search the team's shared memories (hybrid semantic + keyword). Call this before saving a new memory, and before assuming there's no prior art on a task — it surfaces durable team learnings (preferences, feedback, project facts) that aren't in the code.",
            new("object", new() {
                ["query"] = new("string", "What to search for."),
                ["limit"] = new("number", "Max results (default 10, max 50).")
            }, ["query"]), McpToolAnnotations.Read),
        new("get_memory",
            "Fetch a memory's full content by id or slug. Slug resolution precedence: your memories, then your teams', then org-wide; repo-scoped before global. The result's version is what update_memory takes as expected_version. On a log (form: 'log'), content is its header and entries holds its open entries, newest first, then recently settled ones; open_next and settled_next, when present, continue them through list_memory_entries.",
            new("object", new() {
                ["id_or_slug"] = new("string", "Memory id (32 hex) or slug.")
            }, ["id_or_slug"]), McpToolAnnotations.Read),
        new("save_memory",
            "Save a durable learning to the server. Two orthogonal axes. PEOPLE (audience — who can see + edit): 'user' (private), 'team', 'org' (everyone), or 'project' (that project's members — pass audience_project). PLACE (where it surfaces): the cwd's repo by default; a project with project: '<slug>' (surfaces across that project's repos — use it for learnings that span repos instead of saving them org-wide); or the whole org with global: true. project wins over the repo. If the current repo can't be resolved, pass project or global: true, or the save fails. Prefer update_memory when the result reports a nearDuplicate. For an inbox, a decision log or anything else that grows by items, save a log with form: 'log' and add the items with append_memory_entry.",
            new("object", new() {
                ["audience"]         = new("string", "user | team | org | project"),
                ["slug"]             = new("string", "kebab-case identifier, unique within the audience+place pool"),
                ["description"]      = new("string", "One-line summary (max 300 chars)"),
                ["content"]          = new("string", "Full memory body (max 64 KiB); for a log, its header: what the log is for and how to use it (max 8 KiB)"),
                ["kind"]             = new("string", "preference | feedback | project | reference"),
                ["form"]             = new("string", "document (default) | log. A log holds entries appended with append_memory_entry instead of one body; fixed for the memory's life"),
                ["team"]             = new("string", "Team name or id — required for audience 'team' if you are in several teams"),
                ["audience_project"] = new("string", "Project slug — the PEOPLE axis: required for audience 'project'; that project's members become editors (you must be a member). Distinct from 'project' below"),
                ["project"]          = new("string", "Project slug — the PLACE axis: homes the memory at that project so it surfaces across the project's repos (wins over the cwd repo; no membership needed). Distinct from 'audience_project' above"),
                ["global"]           = new("boolean", "true = org-wide, not tied to the current repo (required if not run from a git checkout and no project is given; default: scoped to cwd repo)"),
                ["machine_specific"] = new("boolean", "true = only relevant on this machine (user audience only)")
            }, ["audience", "slug", "description", "content", "kind"]), McpToolAnnotations.Additive),
        new("update_memory",
            "Update an existing memory's description/content/kind (any subset). Content replaces the whole body, so build it from a fresh get_memory and pass that result's version as expected_version: if anyone changed the memory since, nothing is written and the 409 carries the current version and body — merge your change into that body and retry with its version (a 409 without them means call get_memory again first). Omitting expected_version overwrites whatever is there. On a log, content is refused: add to it with append_memory_entry, and change its header with header.",
            new("object", new() {
                ["id"]               = new("string", "Memory id"),
                ["description"]      = new("string", "New one-line summary"),
                ["content"]          = new("string", "New body (a document only)"),
                ["header"]           = new("string", "New header (a log only, max 8 KiB)"),
                ["kind"]             = new("string", "preference | feedback | project | reference"),
                ["expected_version"] = new("string", "The version get_memory returned; the update is refused if the memory changed since")
            }, ["id"]), McpToolAnnotations.Destructive),
        new("rescope_memory",
            "Change a memory's audience — who can see + edit it (promote your user memory to team, org, or a project's members) — or move its home (where it surfaces) to a project, a repo, or the whole org. Two orthogonal axes: audience_project sets the PEOPLE (audience 'project'); project, repo_hash or global: true sets the PLACE — at most one per call. A project move takes precedence over audience; a repo_hash or global move must be sent alone, with no audience, team, audience_project or project — change the audience in a separate call.",
            new("object", new() {
                ["id"]               = new("string", "Memory id"),
                ["audience"]         = new("string", "user | team | org | project"),
                ["team"]             = new("string", "Target team when audience is 'team'"),
                ["audience_project"] = new("string", "Target project slug when audience is 'project' — the PEOPLE axis (its members become editors; you must be a member). Distinct from 'project' below"),
                ["project"]          = new("string", "Target project slug — the PLACE axis: moves the memory's home to that project, surfacing it across the project's repos (takes precedence over audience)"),
                ["repo_hash"]        = new("string", "Target repo hash — the PLACE axis: homes the memory at that single repo. Must be sent alone"),
                ["global"]           = new("boolean", "true = the PLACE axis: homes the memory at the org, not tied to any repo or project. Must be sent alone; false is not a target")
            }, ["id"]), McpToolAnnotations.Destructive),
        new("archive_memory",
            "Archive (soft-delete) a memory.",
            new("object", new() { ["id"] = new("string", "Memory id") }, ["id"]), McpToolAnnotations.Destructive),
        new("append_memory_entry",
            "Append one entry to a log memory (saved with form: 'log'). An append rewrites nothing and never conflicts with another writer, so for an inbox or anything else that grows by items, append here rather than update_memory. If a document memory is being used that way, suggest moving it to a log: archive it, save a log under the same slug with a short header, and append each open item. The entry stays open until settle_memory_entry. Pass an idempotency_key whenever you might retry: a retry with the same key returns the entry the first call appended instead of adding another.",
            new("object", new() {
                ["id_or_slug"]      = new("string", "The log's id (32 hex) or slug"),
                ["title"]           = new("string", "One line (max 200 chars)"),
                ["body"]            = new("string", "The entry (max 16 KiB)"),
                ["idempotency_key"] = new("string", "Any string unique to this entry; the same key on a retry appends nothing new")
            }, ["id_or_slug", "title", "body"]), McpToolAnnotations.Additive),
        new("settle_memory_entry",
            "Mark a log entry dealt with. Any editor of the log may settle any entry. Entries are never edited: to correct one, append a new entry and settle the old. Settling a settled entry changes nothing (changed: false).",
            new("object", new() {
                ["id"]         = new("string", "The log's memory id"),
                ["entry_id"]   = new("string", "Entry id"),
                ["resolution"] = new("string", "One line on how it was dealt with (max 300 chars)")
            }, ["id", "entry_id"]), McpToolAnnotations.Upsert),
        new("reopen_memory_entry",
            "Reopen a settled log entry. Refused when the log already has 200 open entries, and after the entry's tenth reopen. Reopening an open entry changes nothing (changed: false).",
            new("object", new() {
                ["id"]       = new("string", "The log's memory id"),
                ["entry_id"] = new("string", "Entry id")
            }, ["id", "entry_id"]), McpToolAnnotations.Upsert),
        new("list_memory_entries",
            "Page through a log's entries of one status, newest first. get_memory already shows the open entries and recently settled ones; to read past them, pass its open_next (status open) or settled_next (status settled) as before, then each page's next_before. An entry whose body is null was cut to keep the answer bounded; a smaller limit returns it whole.",
            new("object", new() {
                ["id"]     = new("string", "The log's memory id"),
                ["status"] = new("string", "open (default) | settled"),
                ["before"] = new("string", "Cursor from get_memory's open_next / settled_next or a page's next_before"),
                ["limit"]  = new("number", "Page size (default 20, max 50)")
            }, ["id"]), McpToolAnnotations.Read)
    ];
}
