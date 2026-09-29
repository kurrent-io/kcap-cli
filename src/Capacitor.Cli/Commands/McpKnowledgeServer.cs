using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Telemetry;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Commands;

/// <summary>
/// MCP tools over <c>/api/knowledge</c>: read retained facts and skills, and fine-tune skills, one
/// tool per endpoint. Every argument is the server's snake_case wire key, and responses, coded
/// refusals included, pass through as the server wrote them, so an agent branches on the server's
/// codes rather than on anything re-derived here.
/// </summary>
sealed class McpKnowledgeServer(ConfigRoot config, ProfileContext profiles, TokenStore tokens, ICapacitorHttpClient http,
        TelemetryStartup startup, GitProviderRouter router, WorkingDirectory workdir, TimeProvider time) {
    public async Task<int> RunAsync() {
        var baseUrl = profiles.Resolution.ServerUrl!;

        // The repo and the client are resolved by the first tool call: the server is spawned for every
        // session and most never call it, so startup stays local-only.
        var repository = new CwdRepository(config, workdir.Path, router, time);
        var tools      = BuildToolsList();

        var loggedIn = false;
        try { loggedIn = await tokens.LoadForProfileAsync(profiles.Name) is not null; } catch { }

        var telemetry = CliTelemetry.Start(startup with { Command = "mcp-server" }, config, time);
        telemetry.AddSharedProperty("logged_in", loggedIn);

        await using var mcp = new McpTelemetry(telemetry);

        var urlOk = HttpClientExtensions.IsAcceptableUrl(baseUrl);
        HttpClient? client = null;

        async Task<string> DispatchToolCallAsync(JsonNode callId, JsonObject callRequest) {
            if (!urlOk)
                return BuildToolResult(callId, HttpClientExtensions.SchemeMissingHint, isError: true);

            try {
                if (client is null) {
                    client = await http.ForSessionAsync();
                    client.Timeout = Timeout.InfiniteTimeSpan;
                }
                return await HandleToolCallAsync(callId, callRequest, client, baseUrl, await repository.GetHashAsync());
            } catch (Exception ex) {
                // The detail goes to stderr, not the client: an IO error's message can carry local paths.
                await Console.Error.WriteLineAsync($"kcap mcp knowledge: unexpected error handling tools/call: {ex}");
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
                mcp.ToolCalled("kcap-knowledge", tool, ok, CommandTiming.ElapsedMs(start, time));
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
                    request = JsonNode.Parse(line) as JsonObject;
                } catch {
                    continue;
                }
                if (request is null) continue;

                var id     = request["id"];
                var method = McpWorkItemsServer.DecodeMethod(request);
                if (id is null) continue;

                var response = method switch {
                    null         => BuildErrorResponse(id, -32600, "Invalid request: method must be a string"),
                    "initialize" => BuildInitializeResponse(id, request),
                    "tools/list" => ToResponse(id, new McpToolsResult(tools), McpJsonContext.Default.McpToolsResult),
                    "tools/call" => await TimedDispatchToolCallAsync(id, request),
                    _            => McpProtocol.TryHandleStandardMethod(method, id)
                                    ?? BuildErrorResponse(id, -32601, $"Method not found: {method}")
                };

                await writer.WriteLineAsync(response);
            }
        } finally {
            if (client is not null) {
                try { client.Dispose(); } catch { }
            }
        }

        return 0;
    }

    internal static readonly TimeSpan RequestBudget = TimeSpan.FromSeconds(100);

    const string ServerInstructions =
        "Use these tools to read the team's retained facts and curated skills, and to fine-tune a skill. " +
        "Every fact and skill member carries its curation_key and curation: the address and the current state a " +
        "curate_fact_cluster call is built from. Skill writes take the doc_revision get_skill returned as " +
        "expected_doc_revision; a 409 doc_revision_mismatch names the current one. A skill's audience and " +
        "applicability never change in place: curating a member's audience moves it to another skill later.";

    static string BuildInitializeResponse(JsonNode id, JsonObject request) =>
        ToResponse<McpInitResult>(
            id,
            new(McpProtocol.NegotiateVersion(request), new(new()), new("kcap-knowledge", "1.0.0"), ServerInstructions),
            McpJsonContext.Default.McpInitResult);

    internal async Task<string> HandleToolCallAsync(
            JsonNode id, JsonObject request, HttpClient client, string baseUrl, string? cwdRepoHash) {
        var paramsNode = request["params"] as JsonObject;
        var toolName   = paramsNode?["name"] is JsonValue name && name.TryGetValue<string>(out var n) ? n : null;
        var arguments  = paramsNode?["arguments"] as JsonObject;

        if (toolName is null) return BuildErrorResponse(id, -32602, "Missing params.name");

        using var budget = new CancellationTokenSource(RequestBudget, time);
        var ct = budget.Token;

        try {
            using var response = toolName switch {
                "list_skills"          => await client.GetAsync(BuildListSkillsUrl(baseUrl, arguments, cwdRepoHash), ct),
                "get_skill"            => await client.GetAsync(BuildGetSkillUrl(baseUrl, arguments, cwdRepoHash), ct),
                "list_facts"           => await client.GetAsync(BuildListFactsUrl(baseUrl, arguments, cwdRepoHash), ct),
                "search_facts"         => await client.GetAsync(BuildSearchFactsUrl(baseUrl, arguments, cwdRepoHash), ct),
                "edit_skill_body"      => await client.PostAsync(SkillUrl(baseUrl, arguments, "body"), Json(BuildEditBody(arguments)), ct),
                "transition_skill"     => await client.PostAsync(SkillUrl(baseUrl, arguments, "transition"), Json(BuildTransitionBody(arguments)), ct),
                "adjust_skill_members" => await client.PostAsync(SkillUrl(baseUrl, arguments, "members"), Json(BuildAdjustMembersBody(arguments)), ct),
                "curate_fact_cluster"  => await client.PostAsync($"{baseUrl}/api/knowledge/facts/curate", Json(BuildCurateBody(arguments)), ct),
                _                      => throw new ArgumentException($"Unknown tool: {toolName}")
            };

            var body = await response.Content.ReadAsStringAsync(ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return BuildToolResult(id, await AuthRejectionNotice.ForPersistentUnauthorizedAsync(tokens, profiles.Name, baseUrl, time), isError: true);

            if (!response.IsSuccessStatusCode)
                return BuildToolResult(id, body.Length == 0
                    ? $"Error: HTTP {(int)response.StatusCode} (no body)"
                    : $"Error: HTTP {(int)response.StatusCode} — {body}", isError: true);

            return BuildToolResult(id, body.Length == 0 ? $"OK: HTTP {(int)response.StatusCode}" : body);
        } catch (ArgumentException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        } catch (HttpRequestException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        } catch (OperationCanceledException) when (budget.IsCancellationRequested) {
            return BuildToolResult(id, "Error: the server did not answer in time.", isError: true);
        }
    }

    internal static string BuildListSkillsUrl(string baseUrl, JsonObject? args, string? cwdRepoHash) {
        var qs = ScopeQuery(args, cwdRepoHash);
        AddString(qs, args, "state");
        AddString(qs, args, "category");
        AddString(qs, args, "target");
        AddInt(qs, args, "limit");
        AddString(qs, args, "cursor");
        return $"{baseUrl}/api/knowledge/skills?{string.Join("&", qs)}";
    }

    internal static string BuildGetSkillUrl(string baseUrl, JsonObject? args, string? cwdRepoHash) {
        var docId = DocId(args);
        var qs    = ScopeQuery(args, cwdRepoHash);
        AddFlag(qs, args, "include_members");
        AddFlag(qs, args, "include_versions");
        return $"{baseUrl}/api/knowledge/skills/{docId}?{string.Join("&", qs)}";
    }

    internal static string BuildListFactsUrl(string baseUrl, JsonObject? args, string? cwdRepoHash) {
        var qs = ScopeQuery(args, cwdRepoHash);
        AddString(qs, args, "category");
        AddString(qs, args, "cluster_uid");
        AddInt(qs, args, "limit");
        AddString(qs, args, "cursor");
        return $"{baseUrl}/api/knowledge/facts?{string.Join("&", qs)}";
    }

    internal static string BuildSearchFactsUrl(string baseUrl, JsonObject? args, string? cwdRepoHash) {
        var query = McpToolArguments.RequireString(args, "query");
        var qs    = ScopeQuery(args, cwdRepoHash);
        qs.Add($"query={Uri.EscapeDataString(query)}");
        AddString(qs, args, "category");
        AddInt(qs, args, "limit");
        return $"{baseUrl}/api/knowledge/facts/search?{string.Join("&", qs)}";
    }

    /// <summary>A repo scope with no <c>scope_id</c> is the working directory's repo, and a refusal
    /// when there is none: never a wider scope.</summary>
    static List<string> ScopeQuery(JsonObject? args, string? cwdRepoHash) {
        var scope   = McpToolArguments.OptionalString(args, "scope") ?? "repo";
        var scopeId = McpToolArguments.OptionalString(args, "scope_id");
        if (scope == "repo" && scopeId is null)
            scopeId = cwdRepoHash ?? throw new ArgumentException(
                "Cannot resolve the current repository: run from a git checkout, or pass scope_id (a repo hash).");
        return [$"scope={Uri.EscapeDataString(scope)}", $"scope_id={Uri.EscapeDataString(scopeId ?? "")}"];
    }

    static void AddString(List<string> qs, JsonObject? args, string key) {
        if (McpToolArguments.OptionalString(args, key) is { } value) qs.Add($"{key}={Uri.EscapeDataString(value)}");
    }

    static void AddInt(List<string> qs, JsonObject? args, string key) {
        if (McpToolArguments.TryReadInt(args, key, out var value)) qs.Add($"{key}={value}");
    }

    static void AddFlag(List<string> qs, JsonObject? args, string key) {
        if (Flag(args, key) is { } value) qs.Add($"{key}={(value ? "true" : "false")}");
    }

    static string SkillUrl(string baseUrl, JsonObject? args, string action) =>
        $"{baseUrl}/api/knowledge/skills/{DocId(args)}/{action}";

    internal static JsonObject BuildEditBody(JsonObject? args) => new() {
        ["body"]                  = McpToolArguments.RequireString(args, "body"),
        ["operation_id"]          = McpToolArguments.RequireString(args, "operation_id"),
        ["expected_doc_revision"] = Revision(args) ?? throw MissingRevision(),
    };

    internal static JsonObject BuildTransitionBody(JsonObject? args) {
        var body = new JsonObject {
            ["action"]                = McpToolArguments.RequireString(args, "action"),
            ["operation_id"]          = McpToolArguments.RequireString(args, "operation_id"),
            ["expected_doc_revision"] = Revision(args) ?? throw MissingRevision(),
        };
        if (StringArray(args, "targets") is { } targets) body["targets"] = targets;
        if (Text(args, "edited_body") is { } editedBody) body["edited_body"] = editedBody;
        if (Text(args, "reason") is { } reason) body["reason"] = reason;
        if (McpToolArguments.OptionalString(args, "member_disposition") is { } disposition) body["member_disposition"] = disposition;
        return body;
    }

    /// <summary>Only <c>exclude</c> / <c>include</c> take the token, and the server refuses one
    /// missing there, so it rides along when given rather than being required here.</summary>
    internal static JsonObject BuildAdjustMembersBody(JsonObject? args) {
        var body = new JsonObject {
            ["action"]       = McpToolArguments.RequireString(args, "action"),
            ["cluster_uids"] = StringArray(args, "cluster_uids") is { Count: > 0 } uids
                                   ? uids
                                   : throw new ArgumentException("'cluster_uids' must be a non-empty array of cluster uids."),
            ["operation_id"] = McpToolArguments.RequireString(args, "operation_id"),
        };
        if (Revision(args) is { } revision) body["expected_doc_revision"] = revision;
        return body;
    }

    internal static JsonObject BuildCurateBody(JsonObject? args) {
        if (args?["curation_key"] is not JsonObject key)
            throw new ArgumentException("'curation_key' is required: pass the curation_key object a fact read or get_skill member returned.");
        var body = new JsonObject {
            ["curation_key"] = new JsonObject {
                ["source_repo_hash"] = McpToolArguments.RequireString(key, "source_repo_hash"),
                ["category"]         = McpToolArguments.RequireString(key, "category"),
                ["cluster_id"]       = McpToolArguments.RequireString(key, "cluster_id"),
            },
            ["operation_id"]  = McpToolArguments.RequireString(args, "operation_id"),
            ["audience_kind"] = McpToolArguments.RequireString(args, "audience_kind"),
            ["audience_id"]   = Text(args, "audience_id") ?? "",
        };

        if (Text(args, "curated_text") is { } curatedText) body["curated_text"] = curatedText;
        if (StringArray(args, "target_kinds", allowBlank: true) is { } targetKinds) body["target_kinds"] = targetKinds;
        if (StringArray(args, "applies_to_vendors") is { } vendors) body["applies_to_vendors"] = vendors;
        if (StringArray(args, "applies_to_session_kinds") is { } sessionKinds) body["applies_to_session_kinds"] = sessionKinds;
        if (StringArray(args, "applies_to_flow_roles") is { } flowRoles) body["applies_to_flow_roles"] = flowRoles;
        if (StringArray(args, "applies_to_platforms") is { } platforms) body["applies_to_platforms"] = platforms;
        if (Text(args, "status") is { } status) body["status"] = status;
        if (Text(args, "reason") is { } reason) body["reason"] = reason;
        if (McpToolArguments.OptionalString(args, "target_scope_kind") is { } scopeKind) body["target_scope_kind"] = scopeKind;
        if (Text(args, "target_scope_id") is { } scopeId) body["target_scope_id"] = scopeId;
        if (Flag(args, "preserve_audience") is { } preserveAudience) body["preserve_audience"] = preserveAudience;
        if (Flag(args, "preserve_decision") is { } preserveDecision) body["preserve_decision"] = preserveDecision;
        return body;
    }

    static string DocId(JsonObject? args) =>
        Guid.TryParse(McpToolArguments.RequireString(args, "doc_id"), out var docId)
            ? docId.ToString("D")
            : throw new ArgumentException("'doc_id' must be a skill's doc id (a uuid).");

    /// <summary>A string passed through untrimmed, with <c>""</c> kept: an org <c>target_scope_id</c>
    /// and an everyone <c>audience_id</c> are empty strings, not absent ones.</summary>
    static string? Text(JsonObject? args, string key) {
        var node = args?[key];
        if (node is null) return null;
        return node is JsonValue v && v.TryGetValue<string>(out var value)
            ? value
            : throw new ArgumentException($"'{key}' must be a string.");
    }

    /// <summary>A JSON array of strings, or null when absent. An empty array is kept: on an
    /// applicability axis <c>[]</c> means everywhere, while absent means unset. Blank entries are
    /// refused unless <paramref name="allowBlank"/>: the server drops blank promotion targets but
    /// refuses a blank axis value.</summary>
    static JsonArray? StringArray(JsonObject? args, string key, bool allowBlank = false) {
        var node = args?[key];
        if (node is null) return null;
        if (node is not JsonArray array) throw new ArgumentException($"'{key}' must be an array of strings.");
        var result = new JsonArray();
        foreach (var element in array) {
            if (element is not JsonValue v || !v.TryGetValue<string>(out var value))
                throw new ArgumentException($"'{key}' must contain only strings.");
            if (!allowBlank && string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"'{key}' must contain only non-blank strings.");
            // The non-generic Add(JsonNode?) overload: the generic one needs dynamic code under AOT.
            result.Add((JsonNode?)JsonValue.Create(value));
        }
        return result;
    }

    static long? Revision(JsonObject? args) =>
        McpToolArguments.TryReadLong(args, "expected_doc_revision", out var revision) ? revision : null;

    static ArgumentException MissingRevision() =>
        new("'expected_doc_revision' is required: pass the doc_revision get_skill or the last write returned.");

    static bool? Flag(JsonObject? args, string key) {
        var node = args?[key];
        if (node is null) return null;
        return node is JsonValue v && v.TryGetValue<bool>(out var value)
            ? value
            : throw new ArgumentException($"'{key}' must be a boolean.");
    }

    static StringContent Json(JsonObject body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

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

    static McpSchemaProperty Strings(string description) => new("array", description, new("string", "One value."));

    static readonly McpSchemaProperty ScopeProperty   = new("string", "repo (default), project or org. Skill tools take repo only.");
    static readonly McpSchemaProperty ScopeIdProperty = new("string", "Repo hash (defaults to the current repository), a project id or slug, or empty for org.");
    static readonly McpSchemaProperty DocIdProperty   = new("string", "The skill's doc_id.");
    static readonly McpSchemaProperty OperationIdProperty =
        new("string", "A fresh unique id for this change (at most 128 characters, not starting with the reserved found:, draft: or pin-remove:); reuse it only to retry the same call.");
    static readonly McpSchemaProperty CurateOperationIdProperty =
        new("string", "A fresh unique id for this change (not starting with the reserved approve-nomination:); reuse it only to retry the same call.");
    static readonly McpSchemaProperty RevisionProperty =
        new("integer", "The doc_revision get_skill (or the last write) returned; a stale one is refused 409 doc_revision_mismatch naming the current revision.");

    internal static McpTool[] BuildToolsList() => [
        new("list_skills",
            "List a repo's skills (curated guidance docs), most recently changed first. state: forming | candidate | curated | stale | revoked | serving (default: everything not revoked). target filters approved snapshots only: a never-approved skill has no targets and is always listed. doc_revision here is a hint and may be null: take a write token from get_skill. Page with limit and cursor, passing next_cursor back unchanged.",
            new("object", new() {
                ["scope"]    = ScopeProperty,
                ["scope_id"] = ScopeIdProperty,
                ["state"]    = new("string", "forming | candidate | curated | stale | revoked | serving"),
                ["category"] = new("string", "Only skills of this category."),
                ["target"]   = new("string", "injection | skill | display"),
                ["limit"]    = new("integer", "Page size (default 50, max 200)."),
                ["cursor"]   = new("string", "next_cursor from the previous page."),
            }, []), McpToolAnnotations.Read),
        new("get_skill",
            "Read one skill from its authoritative history: status, bodies, doc_revision (the token every skill write takes as expected_doc_revision), latest_version, current members (each with curation_key, curation and excluded, or vanished when its cluster is gone), every assignment pin with its status, and optionally the version history.",
            new("object", new() {
                ["doc_id"]           = DocIdProperty,
                ["scope"]            = ScopeProperty,
                ["scope_id"]         = ScopeIdProperty,
                ["include_members"]  = new("boolean", "Include current members (default true)."),
                ["include_versions"] = new("boolean", "Include the approved version history (default false)."),
            }, ["doc_id"]), McpToolAnnotations.Read),
        new("list_facts",
            "List retained facts homed at a scope (repo by default: the current repository; project: a project id or slug; org: scope_id empty), newest first. Each fact carries its live cluster's cluster_uid, curation_key and curation: the address and current state a curate_fact_cluster call is built from. cluster_uid filters to one live cluster, including facts born in clusters it absorbed. Provenance is omitted where you cannot see the session it came from. Page with limit and cursor, passing next_cursor back unchanged.",
            new("object", new() {
                ["scope"]       = ScopeProperty,
                ["scope_id"]    = ScopeIdProperty,
                ["category"]    = new("string", "Only facts of this category."),
                ["cluster_uid"] = new("string", "Only facts of this live cluster (a uuid)."),
                ["limit"]       = new("integer", "Page size (default 50, max 200)."),
                ["cursor"]      = new("string", "next_cursor from the previous page."),
            }, []), McpToolAnnotations.Read),
        new("search_facts",
            "Find the retained facts nearest a query by meaning, homed at a scope (repo by default). Returns ranked hits with similarity, each carrying cluster_uid, curation_key and curation. 503 search_unavailable when the server has no embedding provider.",
            new("object", new() {
                ["query"]    = new("string", "What to search for."),
                ["scope"]    = ScopeProperty,
                ["scope_id"] = ScopeIdProperty,
                ["category"] = new("string", "Only facts of this category."),
                ["limit"]    = new("integer", "Max results (default 10, max 50)."),
            }, ["query"]), McpToolAnnotations.Read),
        new("edit_skill_body",
            "Replace a skill's pending draft body. Only an existing draft can be edited (409 no_pending_draft otherwise). The drafter may regenerate an unapproved draft when members change, so approve to make an edit durable. A retry with the same operation_id is safe; reusing one for a different body is not detected and replays the first write.",
            new("object", new() {
                ["doc_id"]                = DocIdProperty,
                ["body"]                  = new("string", "The new draft body (markdown)."),
                ["operation_id"]          = OperationIdProperty,
                ["expected_doc_revision"] = RevisionProperty,
            }, ["doc_id", "body", "operation_id", "expected_doc_revision"]), McpToolAnnotations.Destructive),
        new("transition_skill",
            "Move a skill through its lifecycle; a status the action does not admit answers 409 invalid_transition. approve: a candidate or stale skill with a pending draft; targets from injection | skill | display (display alone and only for human_guidance; skill is refused on a skill restricted by session kind or flow role, 422 invalid_targets); optional edited_body. approve answers 409 draft_basis_stale when members changed since the draft, 409 explicit_body_required after an exclude or include (pass an edited_body written without the excluded guidance), 422 zero_effective_members when every member is excluded, and 422 body_exceeds_injection_budget when an injection target's body is too long. reject: drop a stale skill's pending draft. dismiss: a candidate; optional reason. revoke: a curated or stale skill; member_disposition suppress | release is required. restore: a revoked skill; always back to forming, so approve a fresh draft to serve again.",
            new("object", new() {
                ["doc_id"]                = DocIdProperty,
                ["action"]                = new("string", "approve | reject | dismiss | revoke | restore"),
                ["operation_id"]          = OperationIdProperty,
                ["expected_doc_revision"] = RevisionProperty,
                ["targets"]               = Strings("approve: the delivery targets."),
                ["edited_body"]           = new("string", "approve: the body to approve instead of the pending draft."),
                ["reason"]                = new("string", "dismiss: why (at most 500 characters)."),
                ["member_disposition"]    = new("string", "revoke: suppress | release"),
            }, ["doc_id", "action", "operation_id", "expected_doc_revision"]), McpToolAnnotations.Destructive),
        new("adjust_skill_members",
            "Change a skill's members, one action per call over 1-64 cluster_uids. exclude / include leave current members out of (or back in) the next draft and approval: immediate, reversible, require expected_doc_revision, and refuse a repeated uid (400 invalid_input); the serving snapshot is unchanged until the next approval. Either refuses the whole batch with 422 not_current_member when any uid is not a current member, and exclude answers 422 last_effective_member rather than exclude every member. Excluding members already excluded, or including ones that are not, is a no-op success that records nothing, not even its operation_id. add / remove / clear take no expected_doc_revision and accept repeated uids (each distinct uid answers once); they record assignment pins the topic sweep settles later, so the response gives each uid's recorded status and get_skill.assignment_pins shows the outcome. A pin operation_id is keyed across every skill: reusing one for any other doc, action or uids answers 409 operation_id_reused, so use a fresh UUID each time. To move a cluster out of a serving skill: exclude it, approve with edited_body, then remove it; then add it to the destination skill.",
            new("object", new() {
                ["doc_id"]                = DocIdProperty,
                ["action"]                = new("string", "exclude | include | add | remove | clear"),
                ["cluster_uids"]          = Strings("The clusters' cluster_uid values (uuids)."),
                ["operation_id"]          = OperationIdProperty,
                ["expected_doc_revision"] = RevisionProperty,
            }, ["doc_id", "action", "cluster_uids", "operation_id"]), McpToolAnnotations.Destructive),
        new("curate_fact_cluster",
            "Curate a fact cluster by the curation_key a fact read or skill member returned. Always send audience_kind and audience_id (from the fact's curation). Each status admits only its own facets, and a stray one is refused 400: promoted requires curated_text and target_kinds and may carry the applies_to_* axes (omit = unset, [] = everywhere); dismissed takes only an optional reason; revoked takes no facet. Omitting status is an audience-only save or, with target_scope_kind/target_scope_id, a move: send no target_kinds, axes or reason; with preserve_decision the standing decision is rebuilt server-side and curated_text is the one facet it may carry, for a text edit. A fact's curation carries target_kinds (an empty list on a dismissed or revoked fact) and may carry axes whatever its status, so copy curated_text, target_kinds and the axes from it only when sending status promoted. Success is 204, or 202 pending_approval when the promotion was queued for a curator at the target scope. A skill member refuses decision, applicability or home changes with 409 cluster_topic_claimed naming its doc_id (remove-pin it first); curating a member's audience never changes the skill, but drifts the member out to another skill later.",
            new("object", new() {
                ["curation_key"]             = new("object", "{ source_repo_hash, category, cluster_id } exactly as a read returned it."),
                ["operation_id"]             = CurateOperationIdProperty,
                ["audience_kind"]            = new("string", "everyone | team | user"),
                ["audience_id"]              = new("string", "Empty for everyone (the default); the team or user id otherwise."),
                ["curated_text"]             = new("string", "promoted: the promoted text; with status omitted, only alongside preserve_decision."),
                ["target_kinds"]             = Strings("Promotion targets: claude_md | memory | injection (promoted only)."),
                ["applies_to_vendors"]       = Strings("Vendor restriction; [] = everywhere, omit = unset (promoted only)."),
                ["applies_to_session_kinds"] = Strings("Session-kind restriction; [] = everywhere, omit = unset (promoted only)."),
                ["applies_to_flow_roles"]    = Strings("Flow-role restriction; [] = everywhere, omit = unset (promoted only)."),
                ["applies_to_platforms"]     = Strings("Platform restriction; [] = everywhere, omit = unset (promoted only)."),
                ["status"]                   = new("string", "promoted | dismissed | revoked; omit to keep the decision."),
                ["reason"]                   = new("string", "dismissed only: why."),
                ["target_scope_kind"]        = new("string", "Move home: repo | project | org (with target_scope_id)."),
                ["target_scope_id"]          = new("string", "The target home's id; empty for org."),
                ["preserve_audience"]        = new("boolean", "Keep the cluster's current audience rather than the one sent."),
                ["preserve_decision"]        = new("boolean", "With status omitted: rebuild the standing decision for a move or text edit."),
            }, ["curation_key", "operation_id", "audience_kind"]), McpToolAnnotations.Destructive),
    ];
}
