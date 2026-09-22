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
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.Cli.Commands;

/// <summary>
/// MCP tools for publishing artefacts — a self-contained HTML page the server hosts and hands back a
/// link to — and for waiting on the answers people give one.
///
/// <para>The tool list is deliberately narrow. An agent's context pays for every schema it carries
/// whether or not it publishes anything, and a surface wide enough to be worth disabling is worse
/// than a narrow one: reading, version history and takedown all live in the web UI, which is where
/// a person is when they need them.</para>
/// </summary>
sealed class McpArtefactsServer(ConfigRoot config, ProfileContext profiles, TokenStore tokens, ICapacitorHttpClient http,
        TelemetryStartup startup, TimeProvider time) {
    internal const string NotLoggedInMessage = AuthRejectionNotice.NotLoggedIn;

    public async Task<int> RunAsync() {
        var baseUrl = profiles.Resolution.ServerUrl!;

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

        // Validate the server_url shape once, locally (pure string check — no network, token,
        // or stderr). Used to fail gracefully instead of hard-exiting mid-request (below).
        var urlOk = HttpClientExtensions.IsAcceptableUrl(baseUrl);

        // Created on demand (not at startup) so a session that never publishes pays no
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
                if (client is null) {
                    client = await http.ForSessionAsync();
                    LiftClientTimeout(client);
                }

                return await HandleToolCallAsync(callId, callRequest, client, baseUrl);
            } catch (Exception ex) {
                // Unexpected: log the detail to stderr (not to the client, which could leak local
                // paths from IO errors) and return a generic tool error, keeping the loop alive.
                await Console.Error.WriteLineAsync($"kcap mcp artefacts: unexpected error handling tools/call: {ex}");
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
                mcp.ToolCalled("kcap-artefacts", tool, ok, CommandTiming.ElapsedMs(start, time));
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

                request = TryParseRequest(line);

                if (request is null) continue;

                var id     = request["id"];
                var method = McpWorkItemsServer.DecodeMethod(request);

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

    // Server-level usage preamble (MCP `instructions`) — the two things an agent gets wrong without
    // it: publishing a page that silently renders blank because it expected the network, and
    // publishing to nobody because an audience is not the default.
    internal const string ServerInstructions =
        "Use these tools to publish a self-contained HTML page and get a shareable link back — a plan, " +
        "a report, a comparison, anything a person would rather read as a page than as terminal output. " +
        "The page is served under a sandbox that cannot reach the network: inline every style, script " +
        "and image as a data URI, because an external URL will silently render as nothing. An artefact " +
        "is private to its owner until you say otherwise, so pass visibility (and grants, under " +
        "'scoped') when you mean other people to open it. Publishing a revision of something you " +
        "already published is publish_artefact with update_id — it keeps the same URL, so a link you " +
        "already gave someone stays good.";

    static string BuildInitializeResponse(JsonNode id, JsonObject request) =>
        ToResponse<McpInitResult>(
            id,
            new(McpProtocol.NegotiateVersion(request), new(new()), new("kcap-artefacts", "1.0.0"), ServerInstructions),
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

        // The wait gets a budget past the server's own ceiling, so it ends because the SERVER decided
        // it had — never because this side gave up first and left the agent unable to tell a
        // timeout from a lost answer.
        using var budget = new CancellationTokenSource(
            toolName == "await_artefact_responses" ? ClientWaitBudget : RequestBudget, time);

        var ct = budget.Token;

        try {
            using var httpResponse = toolName switch {
                "publish_artefact" => await PublishAsync(client, baseUrl, arguments, ct),

                "list_my_artefacts" => await client.GetAsync($"{baseUrl}/api/artefacts", ct),

                "set_artefact_visibility" => await client.PutAsync(
                    ArtefactUrl(baseUrl, arguments, "visibility"), ToJsonContent(BuildVisibilityBody(arguments)), ct),

                "await_artefact_responses" => await client.GetAsync(WaitUrl(baseUrl, arguments), ct),

                "get_artefact_results" => await client.GetAsync(
                    $"{ArtefactUrl(baseUrl, arguments, "results")}{VersionQuery(arguments)}", ct),

                "close_artefact_responses" => await client.PostAsync(
                    ArtefactUrl(baseUrl, arguments, "responses/close"), ToJsonContent(BuildCloseBody(arguments)), ct),

                _ => throw new ArgumentException($"Unknown tool: {toolName}")
            };

            var body = await httpResponse.Content.ReadAsStringAsync(ct);

            if (httpResponse.StatusCode == HttpStatusCode.Unauthorized) {
                return BuildToolResult(id, await AuthRejectionNotice.ForPersistentUnauthorizedAsync(tokens, profiles.Name, baseUrl, time), isError: true);
            }

            // Forbidden and NotFound are named rather than shown as a bare status: the server keeps
            // them apart on purpose (see IArtefactsApi), and "not found" over a 403 would send an
            // agent off re-publishing something that already exists.
            if (httpResponse.StatusCode == HttpStatusCode.Forbidden) {
                return BuildToolResult(id, "Error: that artefact is not yours to change.", isError: true);
            }

            if (httpResponse.StatusCode == HttpStatusCode.NotFound) {
                return BuildToolResult(id, "Error: no such artefact, or it is not visible to this profile.", isError: true);
            }

            if (!httpResponse.IsSuccessStatusCode) {
                return BuildToolResult(id, $"Error: HTTP {(int)httpResponse.StatusCode} — {body}", isError: true);
            }

            return BuildToolResult(id, body);
        } catch (ArgumentException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        } catch (HttpRequestException ex) {
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        } catch (OperationCanceledException) when (budget.IsCancellationRequested) {
            return BuildToolResult(id, "Error: the server did not answer in time.", isError: true);
        } catch (IOException ex) {
            // A `path` the agent named that cannot be read. Its own message carries the path it
            // already knows, so nothing local leaks that it did not supply.
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        }
    }

    static async Task<HttpResponseMessage> PublishAsync(HttpClient client, string baseUrl, JsonObject? args, CancellationToken ct) {
        var (url, body) = BuildPublishRequest(baseUrl, args, ResolveHtml(args));

        return await client.PostAsync(url, ToJsonContent(body), ct);
    }

    /// <summary>
    /// A publish is either a new artefact or a new version of one, chosen by <c>update_id</c>.
    ///
    /// <para>An <c>update_id</c> that is present must name an artefact: falling back to a create
    /// would hand the agent a second page and a second URL while it believes it revised the
    /// first. A version carries content and its own response schema only, so an audience passed
    /// alongside is refused rather than dropped.</para>
    /// </summary>
    internal static (string Url, JsonObject Body) BuildPublishRequest(string baseUrl, JsonObject? args, string html) {
        if (args?["update_id"] is null) return ($"{baseUrl}/api/artefacts", BuildPublishBody(args, html));

        var url = ArtefactUrl(baseUrl, args, "versions", idKey: "update_id");

        if (args["visibility"] is not null || args["grants"] is not null)
            throw new ArgumentException(
                "'visibility' and 'grants' are not read with 'update_id' — change the audience with set_artefact_visibility.");

        var body = new JsonObject { ["html"] = html };

        if (ReadResponseSchema(args) is { } schema) body["response_schema"] = schema;

        return (url, body);
    }

    /// <summary>
    /// The page itself, from exactly one of <c>html</c> or <c>path</c>.
    ///
    /// <para>Both together is refused rather than resolved by precedence: an agent that supplied
    /// both believes one of them is what gets published, and picking silently means half the time it
    /// publishes something nobody asked for.</para>
    /// </summary>
    internal static string ResolveHtml(JsonObject? args) {
        var html = McpToolArguments.OptionalString(args, "html");
        var path = McpToolArguments.OptionalString(args, "path");

        if (html is { Length: > 0 } && path is { Length: > 0 })
            throw new ArgumentException("Pass either 'html' or 'path', not both.");

        if (html is { Length: > 0 }) return html;

        if (path is not { Length: > 0 })
            throw new ArgumentException("Pass the page as 'html', or name a local file with 'path'.");

        if (!File.Exists(path)) throw new ArgumentException($"No such file: {path}");

        var fromFile = File.ReadAllText(path);

        if (string.IsNullOrWhiteSpace(fromFile)) throw new ArgumentException($"{path} is empty.");

        return fromFile;
    }

    // NOTE: request bodies use snake_case keys — the server's global JSON policy is
    // JsonNamingPolicy.SnakeCaseLower. Responses are passed through as raw text, so only these
    // builders are affected. The rules the server owns — the audience vocabulary, every size and
    // count ceiling, whether a cited session is one this caller may cite — are left to it and
    // surface as coded 4xx bodies through HandleToolCallAsync, rather than being restated here
    // where they would drift.
    internal static JsonObject BuildPublishBody(JsonObject? args, string html) {
        var body = new JsonObject {
            ["title"] = McpToolArguments.RequireString(args, "title"),
            ["html"]  = html
        };

        if (McpToolArguments.OptionalString(args, "description") is { Length: > 0 } description) body["description"] = description;
        if (McpToolArguments.OptionalString(args, "visibility") is { Length: > 0 } visibility) body["visibility"] = visibility;

        if (ReadGrants(args) is { } grants) body["grants"] = grants;

        if (ReadResponseSchema(args) is { } schema) body["response_schema"] = schema;

        // The session is cited without being asked for: an artefact published mid-session belongs
        // with the session that produced it, and an agent that has to remember to say so mostly
        // won't. An explicit session_ids wins when the caller means a different set.
        if (ReadSessions(args) is { } sessions) body["sources"] = sessions;
        else if (ArgParsing.ResolveSessionIdFromEnv() is { Length: > 0 } ambient)
            body["sources"] = new JsonArray((JsonNode?)JsonValue.Create(ambient));

        return body;
    }

    /// <summary>Forwarded whole rather than reshaped: the server owns every rule about what a schema
    /// may declare, and a second interpretation here would be a second place for them to drift.</summary>
    static JsonNode? ReadResponseSchema(JsonObject? args) {
        if (args?["response_schema"] is not { } schema) return null;

        if (schema is not JsonObject declared) throw new ArgumentException("'response_schema' must be an object.");

        return declared.DeepClone();
    }

    internal static JsonObject BuildVisibilityBody(JsonObject? args) {
        var body = new JsonObject { ["visibility"] = McpToolArguments.RequireString(args, "visibility") };

        if (ReadGrants(args) is { } grants) body["grants"] = grants;

        return body;
    }

    /// <summary>
    /// Reads the <c>grants</c> argument into the server's wire shape. Returns null when the caller
    /// did not supply the key at all — absence means "leave the audience to the visibility tier",
    /// while an explicit empty array means "this artefact is granted to nobody", and the two must
    /// not collapse into each other.
    /// </summary>
    internal static JsonArray? ReadGrants(JsonObject? args) {
        if (args is null || !args.TryGetPropertyValue("grants", out var node)) return null;

        if (node is null) throw new ArgumentException("'grants' must be an array of objects, not null.");

        if (node is not JsonArray array) throw new ArgumentException("'grants' must be an array of objects.");

        var result = new JsonArray();

        foreach (var element in array) {
            if (element is not JsonObject grant)
                throw new ArgumentException("'grants' must contain only objects with grant_type and grantee_id.");

            var type = McpToolArguments.RequireString(grant, "grant_type");
            var id   = McpToolArguments.RequireString(grant, "grantee_id");

            // The name is display only and an agent has no directory to look one up in; the id is
            // the honest stand-in, and the server replaces it with the real name when it knows one.
            var name = McpToolArguments.OptionalString(grant, "grantee_name") is { Length: > 0 } supplied ? supplied : id;

            result.Add((JsonNode?)new JsonObject {
                ["grant_type"]   = type,
                ["grantee_id"]   = id,
                ["grantee_name"] = name
            });
        }

        return result;
    }

    internal static JsonArray? ReadSessions(JsonObject? args) {
        if (args is null || !args.TryGetPropertyValue("session_ids", out var node)) return null;

        if (node is null) throw new ArgumentException("'session_ids' must be an array of strings, not null.");

        var raw    = McpWorkItemsServer.ReadStringArray(node, "session_ids");
        var result = new JsonArray();

        foreach (var element in raw) {
            var value = element!.GetValue<string>();

            // Canonicalized the way the server files sessions, so a dashed GUID pasted from a UI
            // cites the session the caller meant instead of silently citing nothing.
            var canonical = WorkContextIds.CanonicalSessionId(value)
                         ?? throw new ArgumentException($"'{value}' is not a valid session id.");

            result.Add((JsonNode?)JsonValue.Create(canonical));
        }

        return result;
    }

    /// <summary>Builds an artefact-scoped URL from a REQUIRED id. There is no ambient artefact to
    /// fall back to, and a default here would change the audience of the wrong page.</summary>
    internal static string ArtefactUrl(string baseUrl, JsonObject? args, string suffix, string idKey = "artefact_id") {
        var id = McpToolArguments.RequireString(args, idKey);

        // Escaping alone leaves "." and ".." to walk out of the route.
        if (id is "." or ".." || id.Contains('/') || id.Contains('\\'))
            throw new ArgumentException($"'{idKey}' is not a valid artefact id.");

        return $"{baseUrl}/api/artefacts/{Escape(id)}/{suffix}";
    }

    static string Escape(string id) => Uri.EscapeDataString(id);

    internal static string WaitUrl(string baseUrl, JsonObject? args) {
        var url = new StringBuilder(ArtefactUrl(baseUrl, args, "responses/wait"))
            .Append("?min_respondents=")
            .Append(McpToolArguments.TryReadInt(args, "min_respondents", out var min) ? min : 1);

        if (McpToolArguments.TryReadInt(args, "version", out var version)) url.Append("&version=").Append(version);
        if (McpToolArguments.TryReadInt(args, "timeout_s", out var timeout)) url.Append("&timeout_s=").Append(timeout);

        return url.ToString();
    }

    /// <summary>
    /// Longer than the server's own 25-minute ceiling.
    ///
    /// <para>The server caps how long it will hold one poll and answers 200 with whatever it has, so
    /// a client that timed out first would turn "nobody has answered yet" into an error the agent
    /// cannot distinguish from a broken server.</para>
    /// </summary>
    internal static readonly TimeSpan ClientWaitBudget = TimeSpan.FromMinutes(30);

    /// <summary>What every call but the wait gets — HttpClient's own default, restated because the
    /// shared client has none.</summary>
    internal static readonly TimeSpan RequestBudget = TimeSpan.FromSeconds(100);

    /// <summary>HttpClient's default timeout would abort a wait the server is still holding, and it
    /// applies to the whole client, so it is lifted and each call carries its own budget.</summary>
    internal static void LiftClientTimeout(HttpClient client) => client.Timeout = Timeout.InfiniteTimeSpan;

    static string VersionQuery(JsonObject? args) =>
        McpToolArguments.TryReadInt(args, "version", out var version) ? $"?version={version}" : "";

    internal static JsonObject BuildCloseBody(JsonObject? args) {
        if (!McpToolArguments.TryReadInt(args, "version", out var version))
            throw new ArgumentException("'version' is required.");

        var body = new JsonObject { ["version"] = version };

        // Absent means close. Reopening is the exception, and has to be asked for.
        if (args is not null && args.TryGetPropertyValue("closed", out var node)) {
            if (node is not JsonValue value || !value.TryGetValue<bool>(out var closed))
                throw new ArgumentException("'closed' must be true or false.");

            body["closed"] = closed;
        }

        return body;
    }

    static StringContent ToJsonContent(JsonObject body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    /// <summary>Null for a line the loop must not act on. A JsonObject fills its property table
    /// lazily, so a duplicated key surfaces on the first read rather than in Parse; forcing it here
    /// keeps that inside the guard instead of ending the server.</summary>
    internal static JsonObject? TryParseRequest(string line) {
        try {
            var request = JsonNode.Parse(line)?.AsObject();
            _ = request?.Count;

            return request;
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
        new("publish_artefact",
            "Publish a self-contained HTML page and get back a link anyone you grant access to can open. "
          + "The page runs sandboxed with no network access, so inline every style, script and image as a "
          + "data URI. Pass the page as 'html' or name a local file with 'path'. It is private to you "
          + "unless you set visibility. Use update_id to publish a revision of an artefact you already "
          + "published — the URL stays the same.",
            new("object", new() {
                ["title"]       = new("string", "What this artefact is called, shown in listings and in the browser tab."),
                ["html"]        = new("string", "The complete page. Use this or 'path', not both."),
                ["path"]        = new("string", "A local HTML file to publish instead of inline 'html'."),
                ["description"] = new("string", "One line saying what the artefact is for."),
                ["visibility"]  = new("string", "Who may open it: 'none' (only you, the default), 'org' (anyone in the organization), or 'scoped' (only the grants below)."),
                ["grants"]      = new("array", "Under 'scoped', who may open it.",
                                      new("object", "One audience member: grant_type ('user', 'team' or 'project'), grantee_id, and an optional grantee_name.")),
                ["session_ids"] = new("array", "Sessions this artefact came out of. Defaults to the current kcap-hooked session when omitted.",
                                      new("string", "A session id.")),
                ["update_id"]   = new("string", "Publish a new version of this existing artefact instead of creating one. The URL does not change."),
                ["response_schema"] = new("object",
                    "Makes the page answerable. Declare the fields people may submit — each with an id and a type "
                  + "of 'choice' (one of options), 'multi' (any of options), 'score' (min..max) or 'text' — and the "
                  + "server validates every answer against them and tallies the results. Optionally set results_mode "
                  + "('owner' (default, only you see answers), 'aggregate' (viewers see tallies, no names or text) or "
                  + "'named' (viewers see who said what)), min_responses_to_reveal, and closes_at. Without it, answers "
                  + "are an opaque blob only you can read.")
            }, ["title"]), McpToolAnnotations.Create),

        new("list_my_artefacts",
            "List the artefacts you can see, newest change first — id, title, audience, latest version and URL.",
            new("object", new(), []), McpToolAnnotations.Read),

        new("await_artefact_responses",
            "Wait for people to answer an artefact you published, then read what they said. Blocks until "
          + "min_respondents distinct people have answered the version, or you close it, or the timeout "
          + "elapses — a timeout is not an error, it returns what there is so far. This is the human "
          + "checkpoint: publish a plan or a decision, share it, then wait here for the answer.",
            new("object", new() {
                ["artefact_id"]     = new("string", "The artefact to wait on."),
                ["version"]         = new("integer", "Which version's answers to wait for. Defaults to the latest."),
                ["min_respondents"] = new("integer", "How many distinct people must have answered before this returns. Defaults to 1."),
                ["timeout_s"]       = new("integer", "How long to wait, in seconds. Defaults to 300; the server caps one wait at 1500 and you may call again.")
            }, ["artefact_id"]), McpToolAnnotations.Read),

        new("get_artefact_results",
            "Read an artefact's answers without waiting: per-field tallies, and each person's current "
          + "answer with their name. Every submit is kept, but this shows the latest per person — "
          + "someone who changed their mind counts once.",
            new("object", new() {
                ["artefact_id"] = new("string", "The artefact to read."),
                ["version"]     = new("integer", "Which version's answers to read. Defaults to the latest.")
            }, ["artefact_id"]), McpToolAnnotations.Read),

        new("close_artefact_responses",
            "Close a version to further answers, freezing its results. Reversible: pass closed=false to "
          + "reopen, which also clears any deadline that was set. Closing also releases anyone blocked "
          + "in await_artefact_responses.",
            new("object", new() {
                ["artefact_id"] = new("string", "The artefact to close."),
                ["version"]     = new("integer", "Which version to close."),
                ["closed"]      = new("boolean", "false reopens. Defaults to true.")
            }, ["artefact_id", "version"]), McpToolAnnotations.Upsert),

        new("set_artefact_visibility",
            "Change who may open an artefact. This replaces the whole audience: a grant left out is one "
          + "being taken away.",
            new("object", new() {
                ["artefact_id"] = new("string", "The artefact whose audience to change."),
                ["visibility"]  = new("string", "'none' (only you), 'org' (anyone in the organization), or 'scoped' (only the grants below)."),
                ["grants"]      = new("array", "Under 'scoped', the complete list of who may open it.",
                                      new("object", "One audience member: grant_type ('user', 'team' or 'project'), grantee_id, and an optional grantee_name."))
            }, ["artefact_id", "visibility"]), McpToolAnnotations.Destructive)
    ];
}
