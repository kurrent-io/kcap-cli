using System.Diagnostics;
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
/// link to. Cloned from <see cref="McpWorkItemsServer"/>'s stdio JSON-RPC loop.
///
/// <para>The tool list is deliberately three tools wide. An agent's context pays for every schema it
/// carries whether or not it publishes anything, and a surface wide enough to be worth disabling is
/// worse than a narrow one: reading, versioning history and takedown all live in the web UI, which
/// is where a person is when they need them.</para>
/// </summary>
sealed class McpArtefactsServer(ConfigRoot config, ProfileContext profiles, TokenStore tokens, ICapacitorHttpClient http) {
    internal const string NotLoggedInMessage = AuthRejectionNotice.NotLoggedIn;

    public async Task<int> RunAsync() {
        var baseUrl = profiles.Resolution.ServerUrl!;

        var tools = BuildToolsList();

        // MCP servers are long-lived and denylisted under the top-level "mcp" command
        // (CommandEvents.Denylisted) — re-initialise under the reportable pseudo-command
        // "mcp-server" so per-tool-call events actually leave. Best-effort: a stale token on
        // disk must never block the server from starting.
        var loggedIn = false;
        try { loggedIn = await tokens.LoadForProfileAsync(profiles.Name) is not null; } catch { }
        CliTelemetry.Initialize("mcp-server", baseUrl, loggedIn, config);

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
                client ??= await http.ForSessionAsync();
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
            var start = Stopwatch.GetTimestamp();
            var tool  = McpTelemetry.SafeToolName(callRequest);
            var ok    = false;

            try {
                var response = await DispatchToolCallAsync(callId, callRequest);
                ok = McpTelemetry.ResponseOk(response);
                return response;
            } finally {
                McpTelemetry.ToolCalled("kcap-artefacts", tool, ok, CommandTiming.ElapsedMs(start));
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

        try {
            using var httpResponse = toolName switch {
                "publish_artefact" => await PublishAsync(client, baseUrl, arguments),

                "list_my_artefacts" => await client.GetAsync($"{baseUrl}/api/artefacts"),

                "set_artefact_visibility" => await client.PutAsync(
                    ArtefactUrl(baseUrl, arguments, "visibility"), ToJsonContent(BuildVisibilityBody(arguments))),

                _ => throw new ArgumentException($"Unknown tool: {toolName}")
            };

            var body = await httpResponse.Content.ReadAsStringAsync();

            if (httpResponse.StatusCode == HttpStatusCode.Unauthorized) {
                return BuildToolResult(id, await AuthRejectionNotice.ForPersistentUnauthorizedAsync(tokens, profiles.Name, baseUrl), isError: true);
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
        } catch (IOException ex) {
            // A `path` the agent named that cannot be read. Its own message carries the path it
            // already knows, so nothing local leaks that it did not supply.
            return BuildToolResult(id, $"Error: {ex.Message}", isError: true);
        }
    }

    /// <summary>A publish is either a new artefact or a new version of one, chosen by
    /// <c>update_id</c>. Both carry the same HTML, so the content is resolved once, before the
    /// branch.</summary>
    static async Task<HttpResponseMessage> PublishAsync(HttpClient client, string baseUrl, JsonObject? args) {
        var html = ResolveHtml(args);

        if (args?["update_id"] is { } updateNode) {
            if (updateNode is not JsonValue updateValue || !updateValue.TryGetValue<string>(out var updateId))
                throw new ArgumentException("'update_id' must be a string.");

            if (updateId.Length > 0) {
                var body = new JsonObject { ["html"] = html };

                return await client.PostAsync($"{baseUrl}/api/artefacts/{Escape(updateId)}/versions", ToJsonContent(body));
            }
        }

        return await client.PostAsync($"{baseUrl}/api/artefacts", ToJsonContent(BuildPublishBody(args, html)));
    }

    /// <summary>
    /// The page itself, from exactly one of <c>html</c> or <c>path</c>.
    ///
    /// <para>Both together is refused rather than resolved by precedence: an agent that supplied
    /// both believes one of them is what gets published, and picking silently means half the time it
    /// publishes something nobody asked for.</para>
    /// </summary>
    internal static string ResolveHtml(JsonObject? args) {
        var html = OptionalString(args, "html");
        var path = OptionalString(args, "path");

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
            ["title"] = McpWorkItemsServer.RequireString(args, "title"),
            ["html"]  = html
        };

        if (OptionalString(args, "description") is { Length: > 0 } description) body["description"] = description;
        if (OptionalString(args, "visibility") is { Length: > 0 } visibility) body["visibility"] = visibility;

        if (ReadGrants(args) is { } grants) body["grants"] = grants;

        // The session is cited without being asked for: an artefact published mid-session belongs
        // with the session that produced it, and an agent that has to remember to say so mostly
        // won't. An explicit session_ids wins when the caller means a different set.
        if (ReadSessions(args) is { } sessions) body["sources"] = sessions;
        else if (ArgParsing.ResolveSessionIdFromEnv() is { Length: > 0 } ambient)
            body["sources"] = new JsonArray((JsonNode?)JsonValue.Create(ambient));

        return body;
    }

    internal static JsonObject BuildVisibilityBody(JsonObject? args) {
        var body = new JsonObject { ["visibility"] = McpWorkItemsServer.RequireString(args, "visibility") };

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

            var type = McpWorkItemsServer.RequireString(grant, "grant_type");
            var id   = McpWorkItemsServer.RequireString(grant, "grantee_id");

            // The name is display only and an agent has no directory to look one up in; the id is
            // the honest stand-in, and the server replaces it with the real name when it knows one.
            var name = OptionalString(grant, "grantee_name") is { Length: > 0 } supplied ? supplied : id;

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

    /// <summary>Reads an optional string argument. An absent key is null; a present one of the wrong
    /// shape throws, so a malformed argument fails loudly instead of being dropped.</summary>
    internal static string? OptionalString(JsonObject? args, string key) {
        if (args is null || !args.TryGetPropertyValue(key, out var node) || node is null) return null;

        if (node is not JsonValue value || !value.TryGetValue<string>(out var text))
            throw new ArgumentException($"'{key}' must be a string.");

        return text;
    }

    /// <summary>Builds an artefact-scoped URL from a REQUIRED id. There is no ambient artefact to
    /// fall back to, and a default here would change the audience of the wrong page.</summary>
    internal static string ArtefactUrl(string baseUrl, JsonObject? args, string suffix) {
        var id = McpWorkItemsServer.RequireString(args, "artefact_id");

        // Escaping alone leaves "." and ".." to walk out of the route.
        if (id is "." or ".." || id.Contains('/') || id.Contains('\\'))
            throw new ArgumentException("'artefact_id' is not a valid artefact id.");

        return $"{baseUrl}/api/artefacts/{Escape(id)}/{suffix}";
    }

    static string Escape(string id) => Uri.EscapeDataString(id);

    static StringContent ToJsonContent(JsonObject body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

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
                ["update_id"]   = new("string", "Publish a new version of this existing artefact instead of creating one. The URL does not change.")
            }, ["title"])),

        new("list_my_artefacts",
            "List the artefacts you can see, newest change first — id, title, audience, latest version and URL.",
            new("object", new(), [])),

        new("set_artefact_visibility",
            "Change who may open an artefact. This replaces the whole audience: a grant left out is one "
          + "being taken away.",
            new("object", new() {
                ["artefact_id"] = new("string", "The artefact whose audience to change."),
                ["visibility"]  = new("string", "'none' (only you), 'org' (anyone in the organization), or 'scoped' (only the grants below)."),
                ["grants"]      = new("array", "Under 'scoped', the complete list of who may open it.",
                                      new("object", "One audience member: grant_type ('user', 'team' or 'project'), grantee_id, and an optional grantee_name."))
            }, ["artefact_id", "visibility"]))
    ];
}
