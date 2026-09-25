using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Telemetry;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.WorkItems;

using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

/// <summary>
/// MCP tools for the work-items correlation surface: attach the current session and its
/// continuation chain to a work item, and list what a session is already attached to. It resolves
/// no repo or machine context. The only per-call input is the session id and the declare selector,
/// both carried in the tool arguments.
/// </summary>
sealed class McpWorkItemsServer(ConfigRoot config, ProfileContext profiles, TokenStore tokens, ICapacitorHttpClient http,
        TelemetryStartup startup, TimeProvider time) {
    internal const string NotLoggedInMessage = AuthRejectionNotice.NotLoggedIn;

    public async Task<int> RunAsync() {
        var baseUrl = profiles.Resolution.ServerUrl!;

        var tools = BuildToolsList();

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

        // Created on demand (not at startup) so a session that never calls a tool pays no
        // network/token/stderr cost. Nullable field rather than Lazy<Task> so a transient
        // creation failure leaves it null and the next call retries. Safe without locking:
        // the stdio loop handles one request at a time.
        HttpClient? client = null;

        // Guarded tool dispatch: never let the stdio JSON-RPC loop die on one bad request. An
        // unexpected failure would otherwise bubble out of the loop and kill the server mid-protocol;
        // return a JSON-RPC tool error instead so it keeps serving.
        async Task<string> DispatchToolCallAsync(JsonNode callId, JsonObject callRequest) {
            if (!urlOk)
                return BuildToolResult(callId, HttpClientExtensions.SchemeMissingHint, isError: true);

            try {
                client ??= await http.ForSessionAsync();
                return await HandleToolCallAsync(callId, callRequest, client, baseUrl);
            } catch (Exception ex) {
                // Unexpected: log the detail to stderr (not to the client, which could leak local
                // paths from IO errors) and return a generic tool error, keeping the loop alive.
                await Console.Error.WriteLineAsync($"kcap mcp workitems: unexpected error handling tools/call: {ex}");
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
                mcp.ToolCalled("kcap-workitems", tool, ok, CommandTiming.ElapsedMs(start, time));
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

    // Server-level usage preamble (MCP `instructions`) — steers agents to DECLARE a work item's
    // structure (breakdown + relations, which the server never infers), not just attach to it.
    internal const string ServerInstructions =
        "Use these tools to attach the current session to its SDLC work item AND to declare that work " +
        "item's structure. When you plan or discover that a work item breaks into parts, create the part " +
        "items (declare_work_item with new_title) and declare the parent→parts breakdown " +
        "(declare_work_breakdown); when one item must land before another, declare the dependency " +
        "(declare_work_relation — 'blocks'/'blocked_by'). Breakdown and relations are DECLARED, never " +
        "inferred: if you don't declare them the work item's topology stays empty. Declare only real " +
        "structure you're confident of — every item must be visible to you, but parts and dependencies " +
        "may cross repositories — and use the retract_* tools when it changes. Two items for the same " +
        "work — a title-only item you created and the issue/PR-keyed item the server minted — are a " +
        "duplicate: merge yours into the keyed one with merge_work_item. A wrong attach is undone with " +
        "detach_work_item, never papered over with a breakdown. Work you leave unfinished goes in with " +
        "declare_loose_end — one call per concrete item, and never a 'none'.";

    static string BuildInitializeResponse(JsonNode id, JsonObject request) =>
        ToResponse<McpInitResult>(
            id,
            new(McpProtocol.NegotiateVersion(request), new(new()), new("kcap-workitems", "1.0.0"), ServerInstructions),
            McpJsonContext.Default.McpInitResult
        );

    static string BuildToolsListResponse(JsonNode id, McpTool[] tools) =>
        ToResponse(id, new McpToolsResult(tools), McpJsonContext.Default.McpToolsResult);

    internal async Task<string> HandleToolCallAsync(
            JsonNode   id,
            JsonObject request,
            HttpClient client,
            string     baseUrl
        ) {
        var paramsNode = request["params"]?.AsObject();
        var toolName   = paramsNode?["name"]?.GetValue<string>();
        var arguments  = paramsNode?["arguments"]?.AsObject();

        if (toolName is null) {
            return BuildErrorResponse(id, -32602, "Missing params.name");
        }

        try {
            using var httpResponse = toolName switch {
                "declare_work_item"      => await client.PostAsync($"{baseUrl}/api/work-items/declare", ToJsonContent(BuildDeclareBody(arguments))),
                "get_session_work_items" => await client.GetAsync(BuildSessionUrl(baseUrl, arguments)),
                "declare_loose_end"      => await client.PostAsync($"{baseUrl}/api/loose-ends/declare", ToJsonContent(BuildDeclareLooseEndBody(arguments))),

                // The declared breakdown/relation surface. Every id is a
                // REQUIRED argument here, unlike session_id: there is no ambient "current work item"
                // to fall back to, and guessing one would attach the wrong graph edge.
                "declare_work_breakdown" => await client.PostAsync(
                    ItemUrl(baseUrl, arguments, "parent_id", "breakdown"), ToJsonContent(BuildBreakdownBody(arguments))),
                "retract_work_breakdown" => await client.PostAsync(
                    ItemUrl(baseUrl, arguments, "parent_id", "breakdown/retract"), ToJsonContent(BuildBreakdownBody(arguments))),
                "declare_work_relation"  => await client.PostAsync(
                    ItemUrl(baseUrl, arguments, "from_id", "relations"), ToJsonContent(BuildRelationBody(arguments))),
                "retract_work_relation"  => await client.PostAsync(
                    ItemUrl(baseUrl, arguments, "from_id", "relations/retract"), ToJsonContent(BuildRelationBody(arguments))),
                "get_work_item_topology" => await client.GetAsync(
                    ItemUrl(baseUrl, arguments, "work_item_id", "topology")),

                // The corrections: the merged-away / detached item is the route, like the topology
                // verbs; the survivor and the session ride in the body.
                "merge_work_item"        => await client.PostAsync(
                    ItemUrl(baseUrl, arguments, "work_item_id", "merge"), ToJsonContent(BuildMergeBody(arguments))),
                "detach_work_item"       => await client.PostAsync(
                    ItemUrl(baseUrl, arguments, "work_item_id", "detach"), ToJsonContent(BuildDetachBody(arguments))),

                _                        => throw new ArgumentException($"Unknown tool: {toolName}")
            };

            var body = await httpResponse.Content.ReadAsStringAsync();

            if (httpResponse.StatusCode == HttpStatusCode.Unauthorized) {
                return BuildToolResult(id, await AuthRejectionNotice.ForPersistentUnauthorizedAsync(tokens, profiles.Name, baseUrl, time), isError: true);
            }

            if (!httpResponse.IsSuccessStatusCode) {
                return BuildToolResult(id, $"Error: HTTP {(int)httpResponse.StatusCode} — {body}", isError: true);
            }

            return BuildToolResult(id, body);
        } catch (ArgumentException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        } catch (HttpRequestException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        }
    }

    static StringContent ToJsonContent(JsonObject body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    // NOTE: request bodies use snake_case keys — the server's global JSON policy is
    // JsonNamingPolicy.SnakeCaseLower. Responses are passed through as raw
    // text, so only this request-body builder is affected. The server enforces "exactly one of
    // issue_key/pr_number/work_item_id/new_title" (400 on violation) — this builder passes
    // through whichever selector(s) were supplied and lets that validation surface as a tool
    // error via the 4xx-body mapping in HandleToolCallAsync, rather than duplicating the rule
    // client-side.
    internal static JsonObject BuildDeclareBody(JsonObject? args) {
        var body = new JsonObject { ["session_id"] = McpSessionId.Resolve(args) };

        if (args?["issue_key"]?.GetValue<string>() is { Length: > 0 } issueKey) body["issue_key"] = issueKey;
        if (args?["work_item_id"]?.GetValue<string>() is { Length: > 0 } workItemId) body["work_item_id"] = workItemId;
        if (args?["new_title"]?.GetValue<string>() is { Length: > 0 } newTitle) body["new_title"] = newTitle;
        if (McpToolArguments.TryReadInt(args, "pr_number", out var prNumber)) body["pr_number"] = prNumber;

        return body;
    }

    internal static string BuildSessionUrl(string baseUrl, JsonObject? args) =>
        $"{baseUrl}/api/work-items/session/{Uri.EscapeDataString(McpSessionId.Resolve(args))}";

    /// <summary>
    /// Builds a work-item-scoped URL, reading a REQUIRED id from <paramref name="idKey"/>.
    /// Required with no fallback, deliberately: <see cref="McpSessionId.Resolve(JsonObject?)"/> can default to the
    /// ambient session because "the session I am running in" is unambiguous, whereas there is no
    /// ambient work item — a default here would silently attach the wrong edge of the graph.
    /// Escaped, so an id containing a slash or a percent cannot walk out of its path segment and hit
    /// a different route.
    /// </summary>
    internal static string ItemUrl(string baseUrl, JsonObject? args, string idKey, string suffix) {
        // Validation before escaping, and the dot-segment refusal, are WorkContextIds' — escaping
        // alone leaves "." and ".." to walk out of the route.
        var id = WorkContextIds.ValidWorkItemId(McpToolArguments.RequireString(args, idKey))
              ?? throw new ArgumentException($"'{idKey}' is not a valid work item id.");

        return $"{baseUrl}/api/work-items/{Uri.EscapeDataString(id)}/{suffix}";
    }

    // Server-side validation is NOT duplicated here — same reasoning as BuildDeclareBody's note. The
    // rules the server owns (cross-repo edges, unknown/deleted ids, a parent listed among its own
    // parts, self-relations, an empty parts list, the relation_kind vocabulary) all surface as coded
    // 4xx bodies through HandleToolCallAsync. What IS validated locally is SHAPE: a present-but-
    // wrong-typed argument must fail loudly rather than be dropped, because a silently omitted
    // part_ids turns a malformed declare into a differently-shaped request whose rejection reads as
    // if the caller had sent nothing.
    internal static JsonObject BuildBreakdownBody(JsonObject? args) {
        var body = new JsonObject();

        // Presence, not truthiness (review finding): `{"part_ids": null}` is a PRESENT wrong shape,
        // and the `is { } node` form treated it as absence — silently omitting it and turning a
        // malformed declare into a differently-shaped request. Explicit null now fails like any other
        // wrong type.
        if (args is not null && args.TryGetPropertyValue("part_ids", out var node)) {
            if (node is null) throw new ArgumentException("'part_ids' must be an array of strings, not null.");

            body["part_ids"] = ReadStringArray(node, "part_ids");
        }

        return body;
    }

    internal static JsonObject BuildRelationBody(JsonObject? args) {
        var body = new JsonObject();

        // to_id and relation_kind are left to the server to require and to interpret: it owns the
        // vocabulary and the structural rules, and a coded 400 naming the real reason beats a guess
        // made here. Every SUPPLIED string is forwarded verbatim, including "" (review finding): the
        // previous `is { Length: > 0 }` form dropped an explicit empty string, so the caller got the
        // server's "required" error instead of its more useful "invalid value" one. Absence stays
        // absence; a present non-string still fails locally, as shape validation should.
        CopySuppliedString(args, "to_id", body);
        CopySuppliedString(args, "relation_kind", body);

        return body;
    }

    /// <summary>Copies a string argument into the request body if the caller SUPPLIED the key at
    /// all. An empty string is a supplied value and is forwarded; an explicit null or a non-string is
    /// a wrong shape and throws; an absent key is left absent so the server's own "required" error
    /// surfaces rather than a local guess.</summary>
    /// <summary>The survivor travels under the server's name for it (<c>target_id</c>); the tool
    /// names it <c>into_work_item_id</c> so the direction reads unambiguously beside
    /// <c>work_item_id</c>.</summary>
    internal static JsonObject BuildMergeBody(JsonObject? args) =>
        new() { ["target_id"] = McpToolArguments.RequireString(args, "into_work_item_id") };

    internal static JsonObject BuildDetachBody(JsonObject? args) =>
        new() { ["session_id"] = McpSessionId.Resolve(args) };

    // Text bounds and the none-class rule stay the server's, so its 400 names the real reason.
    internal static JsonObject BuildDeclareLooseEndBody(JsonObject? args) =>
        new() { ["session_id"] = McpSessionId.Resolve(args), ["text"] = McpToolArguments.RequireString(args, "text") };

    static void CopySuppliedString(JsonObject? args, string key, JsonObject body) {
        if (args is null || !args.TryGetPropertyValue(key, out var node)) return;

        if (node is null) throw new ArgumentException($"'{key}' must be a string, not null.");

        if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
            throw new ArgumentException($"'{key}' must be a string.");

        body[key] = text;
    }

    /// <summary>Reads a JSON array of non-blank strings. Any other present shape — a bare string, an
    /// object, an array holding a number or a blank — throws, so a malformed argument surfaces as a
    /// validation error instead of being partially dropped.</summary>
    internal static JsonArray ReadStringArray(JsonNode node, string key) {
        if (node is not JsonArray array) throw new ArgumentException($"'{key}' must be an array of strings.");

        var result = new JsonArray();

        foreach (var element in array) {
            if (element is not JsonValue elementValue || !elementValue.TryGetValue<string>(out var value))
                throw new ArgumentException($"'{key}' must contain only strings.");

            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"'{key}' must not contain blank entries.");

            // Cast to JsonNode so the non-generic Add(JsonNode?) overload is chosen —
            // the generic Add<T>(T) trips IL2026/IL3050 under AOT (see CLAUDE.md).
            result.Add((JsonNode?)JsonValue.Create(value));
        }

        return result;
    }

    /// <summary>Decodes the JSON-RPC <c>method</c> field, returning null for a present but
    /// wrong-shaped value (e.g. an object) instead of throwing — a malformed request must yield
    /// an invalid-request response, never terminate the stdio loop.</summary>
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
        new("declare_work_item",
            "Attach the CURRENT session (and its continuation chain) to a work item on the Capacitor server. Provide exactly one of issue_key, pr_number, work_item_id, or new_title.",
            new("object", new() {
                ["issue_key"]    = new("string", "Attach to the work item for this issue, creating it if none exists yet. Accepts a tracker key ('PROJ-1234'), an issue number in this session's repository ('#123'), a qualified reference ('owner/repo#123'), or a GitHub issue URL."),
                ["pr_number"]    = new("integer", "Attach to the work item for this PR number, creating it if none exists yet."),
                ["work_item_id"] = new("string", "Attach directly to this work item id."),
                ["new_title"]    = new("string", "Create a brand-new work item with this title and attach to it."),
                ["session_id"]   = new("string", "Session id to attach. Defaults to the session this server runs in when omitted.")
            }, []), McpToolAnnotations.Additive),
        new("get_session_work_items",
            "List the work items the current session is attached to.",
            new("object", new() {
                ["session_id"] = new("string", "Session id to look up. Defaults to the session this server runs in when omitted.")
            }, []), McpToolAnnotations.Read),

        new("declare_loose_end",
            "Record a loose end — a concrete piece of work this session leaves unfinished (a missing test, "
          + "a TODO, a follow-up) — so it appears in the user's next-work ledger. One call per item, in "
          + "plain text; do not declare 'none'. Requires a session: the current kcap-hooked one by default.",
            new("object", new() {
                ["text"]       = new("string", "The unfinished work, as one plain-text sentence; the server accepts 12-500 characters after normalizing whitespace and case."),
                ["session_id"] = new("string", "Session id to declare against. Defaults to the session this server runs in when omitted.")
            }, ["text"]), McpToolAnnotations.Upsert),

        // The declared work-breakdown / relation surface. NOTE: no tool
        // here accepts `source` or `declared_by`. The server resolves both from the authenticated
        // caller and rejects a `source` of "user" outright, so exposing either would be an argument
        // the server ignores at best and a spoofing surface at worst.
        new("declare_work_breakdown",
            "Declare that a work item is broken down into parts (sub-items). Idempotent: re-declaring an "
          + "existing part is accepted and reported as existing rather than created. A part can have at "
          + "most one parent, and every item must be visible to you — a part may live in a different "
          + "repository than its parent, where repository is display only.",
            new("object", new() {
                ["parent_id"] = new("string", "The work item being broken down."),
                ["part_ids"]  = new("array", "Work item ids that are parts of the parent.", new("string", "A work item id."))
            }, ["parent_id", "part_ids"]), McpToolAnnotations.Upsert),

        new("retract_work_breakdown",
            "Retract a previously declared breakdown, detaching the named parts from the parent.",
            new("object", new() {
                ["parent_id"] = new("string", "The work item whose breakdown is being retracted."),
                ["part_ids"]  = new("array", "Work item ids to detach from the parent.", new("string", "A work item id."))
            }, ["parent_id", "part_ids"]), McpToolAnnotations.Destructive),

        new("declare_work_relation",
            "Declare a dependency between two work items: 'blocks' means from_id blocks to_id, "
          + "'blocked_by' means from_id is blocked by to_id. Both items must be visible to you and may "
          + "live in different repositories; an item cannot relate to itself.",
            new("object", new() {
                ["from_id"]       = new("string", "The work item the relation starts from."),
                ["to_id"]         = new("string", "The work item on the other end of the relation."),
                ["relation_kind"] = new("string", "Either 'blocks' or 'blocked_by'.")
            }, ["from_id", "to_id", "relation_kind"]), McpToolAnnotations.Upsert),

        new("retract_work_relation",
            "Retract a previously declared dependency between two work items.",
            new("object", new() {
                ["from_id"]       = new("string", "The work item the relation starts from."),
                ["to_id"]         = new("string", "The work item on the other end of the relation."),
                ["relation_kind"] = new("string", "Either 'blocks' or 'blocked_by'.")
            }, ["from_id", "to_id", "relation_kind"]), McpToolAnnotations.Destructive),

        new("get_work_item_topology",
            "Read a work item's declared breakdown and relations — its parent, parts, and dependencies. "
          + "Scoped to what the caller can see, so items you have no access to are absent rather than hidden "
          + "placeholders.",
            new("object", new() {
                ["work_item_id"] = new("string", "The work item whose topology to read.")
            }, ["work_item_id"]), McpToolAnnotations.Read),

        new("merge_work_item",
            "Merge a work item INTO another so both read as the survivor: the merged item's sessions and links "
          + "move to the target and it stops appearing on its own. Use it to collapse a duplicate — typically a "
          + "title-only item you created into the issue- or PR-keyed item for the same work (keep the keyed item "
          + "as the target). Repeating a landed merge is a no-op. The server refuses when a user marked either "
          + "item standalone, rejected the pairing, or the items belong to different tracker hierarchies; then "
          + "stop and tell the user rather than retrying — they can merge from the dashboard.",
            new("object", new() {
                ["work_item_id"]      = new("string", "The work item to merge away (the duplicate)."),
                ["into_work_item_id"] = new("string", "The work item that survives (prefer the issue- or PR-keyed one).")
            }, ["work_item_id", "into_work_item_id"]), McpToolAnnotations.Destructive),

        new("detach_work_item",
            "Detach a session from a work item it was wrongly attached to. The removal is durable: automated "
          + "correlation cannot re-attach the pair afterwards; only an explicit declare_work_item can. An "
          + "attachment a user pinned in the dashboard cannot be removed by an agent.",
            new("object", new() {
                ["work_item_id"] = new("string", "The work item to detach the session from."),
                ["session_id"]   = new("string", "Session id to detach. Defaults to the session this server runs in when omitted.")
            }, ["work_item_id"]), McpToolAnnotations.Destructive)
    ];
}
