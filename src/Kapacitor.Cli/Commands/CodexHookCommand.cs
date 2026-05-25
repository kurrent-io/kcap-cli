using System.Text;
using System.Text.Json.Nodes;
using Kapacitor.Cli.Core;

namespace Kapacitor.Cli.Commands;

/// <summary>
/// Single-binary dispatcher for Codex hooks. Codex invokes the same command
/// for every hook event with <c>hook_event_name</c> in the JSON payload, so
/// we collapse the six event handlers behind one CLI entry point rather than
/// minting one subcommand per event the way the Claude path does.
/// </summary>
/// <remarks>
/// Wire contract (Codex event → server route):
///   SessionStart      → POST /hooks/session-start/codex
///   Stop              → no server POST. Codex fires Stop at every turn end,
///                       not session end (AI-648). Session-end is owned by the
///                       watcher's parent-PID monitor (AI-647 — see
///                       WatchCommand.PostSessionEndOnParentExitAsync).
///                       HandleStop only refreshes watcher liveness and emits
///                       {"continue":true} so Codex's hook parser is satisfied.
///   PermissionRequest → in a daemon-launched hosted agent (KAPACITOR_DAEMON_URL set), bounce
///                       through the daemon's LocalPermissionBridge and wait for the dashboard's
///                       decision (fail-closed on bridge errors: deny + exit nonzero). Otherwise:
///                       POST /hooks/permission-record (fire-and-forget; CLI emits no decision so
///                       Codex's normal in-CLI approval prompt takes over).
///   UserPromptSubmit  → swallowed (v1 — neither vendor consumes them)
///   PreToolUse        → swallowed
///   PostToolUse       → swallowed
/// </remarks>
static class CodexHookCommand {
    // Codex's Stop and SessionStart hooks parse stdout as the
    // `stop.command.output` / `session-start.command.output` JSON schema and
    // reject empty bodies with "hook returned invalid stop hook JSON output".
    // `continue: true` is the schema default; emitting it explicitly satisfies
    // the parser without altering behavior. See AI-635.
    const string SessionScopedOutputJson = """{"continue":true}""";

    public static async Task<int> Handle(string baseUrl, TextReader stdin) {
        var body = await stdin.ReadToEndAsync();

        JsonNode? node;

        try {
            node = JsonNode.Parse(body);
        } catch {
            // Best effort — never crash the host CLI on a malformed payload.
            return 0;
        }

        if (node is null) return 0;

        var eventName = TryGetString(node, "hook_event_name");

        if (string.IsNullOrWhiteSpace(eventName)) return 0;

        // Normalize session_id to dashless GUID, inject home_dir, and tag the
        // agent host id when running inside a daemon-spawned agent. Mirrors
        // the Claude hook path in Program.cs (including the disabled-session
        // check below); the plan_content branch remains Claude-specific.
        NormalizeGuidField(node, "session_id");

        node["home_dir"] = PathHelpers.HomeDirectory;

        var agentHostId = Environment.GetEnvironmentVariable("KAPACITOR_AGENT_ID");
        if (agentHostId is not null) {
            node["agent_host_id"] = agentHostId;
        }

        // Mirror the Claude path: if the user ran `kapacitor disable`, skip every
        // server POST and the watcher restart. Without this check the next Codex
        // Stop hook would re-enliven the watcher and re-send transcript data for
        // a session whose data was just deleted server-side.
        var disabledSessionId = TryGetString(node, "session_id");
        if (disabledSessionId is not null && DisabledSessions.IsDisabled(disabledSessionId)) {
            // Emit the session-scoped JSON Codex's Stop/SessionStart parsers expect,
            // then skip dispatch. (Claude's disabled branch also returns immediately
            // — see Program.cs around line 593.)
            if (eventName == "Stop" || eventName == "SessionStart") {
                Console.Write(SessionScopedOutputJson);
            }
            return 0;
        }

        return eventName switch {
            "SessionStart"      => await HandleSessionStart(baseUrl, node),
            "Stop"              => await HandleStop(baseUrl, node),
            "PermissionRequest" => await HandlePermissionRequest(baseUrl, node),
            "UserPromptSubmit"
              or "PreToolUse"
              or "PostToolUse"  => 0,  // v1: swallow informational events
            _                   => 0   // unknown — silently ignore
        };
    }

    static async Task<int> HandleSessionStart(string baseUrl, JsonNode node) {
        var enriched = await RepositoryDetection.EnrichWithRepositoryInfo(node.ToJsonString());

        var exit = await PostHookAsync(baseUrl, "session-start/codex", enriched);
        if (exit != 0) return exit;

        // Emit Codex's required JSON output BEFORE spawning the watcher. The
        // watcher is best-effort and may take time to start; the parent (Codex)
        // is waiting on stdout, and there's nothing the watcher can contribute
        // to this response.
        Console.Write(SessionScopedOutputJson);

        var enrichedNode = JsonNode.Parse(enriched);
        var sessionId    = TryGetString(enrichedNode, "session_id");
        var transcript   = TryGetString(enrichedNode, "transcript_path");
        var cwd          = TryGetString(enrichedNode, "cwd");

        if (sessionId is not null && transcript is not null) {
            await WatcherManager.EnsureWatcherRunning(
                baseUrl, sessionId, transcript,
                agentId: null, sessionIdOverride: null, cwd: cwd,
                skipTitle: false, vendor: "codex"
            );
        }

        return 0;
    }

    static async Task<int> HandleStop(string baseUrl, JsonNode node) {
        // Codex 'Stop' fires at every turn end, NOT session end. Session-end
        // is fired by the watcher's parent-PID monitor in WatchCommand.cs
        // (AI-647) when the codex process actually exits — that path POSTs
        // /hooks/session-end/codex with reason: "parent_exited" and handles
        // generate_whats_done. Treating Stop as session-end here would kill
        // the watcher after turn 1 and mismark multi-turn sessions as ended
        // before they actually finish.
        //
        // Symmetric with Claude's stop/notification branch in Program.cs —
        // we just keep the watcher alive in case it crashed mid-session.
        var sessionId  = TryGetString(node, "session_id");
        var transcript = TryGetString(node, "transcript_path");
        var cwd        = TryGetString(node, "cwd");

        if (sessionId is not null && transcript is not null) {
            await WatcherManager.EnsureWatcherRunning(
                baseUrl, sessionId, transcript,
                agentId: null, sessionIdOverride: null, cwd: cwd,
                skipTitle: false, vendor: "codex"
            );
        }

        // AI-635: Codex's stop-hook output parser rejects empty stdout as
        // "invalid stop hook JSON output". Emit the schema default explicitly.
        Console.Write(SessionScopedOutputJson);
        return 0;
    }

    static async Task<int> HandlePermissionRequest(string baseUrl, JsonNode node) {
        var daemonUrl = Environment.GetEnvironmentVariable("KAPACITOR_DAEMON_URL");

        return daemonUrl is null
            ? await HandlePermissionRequestStub(baseUrl, node)
            : await HandlePermissionRequestViaBridge(daemonUrl, node);
    }

    static async Task<int> HandlePermissionRequestStub(string baseUrl, JsonNode node) {
        // Terminal Codex sessions can't answer a Capacitor UI prompt, so we
        // record the event server-side (best-effort) and emit no decision —
        // Codex falls back to its built-in in-CLI approval flow and the user
        // answers there.
        //
        // Do NOT post to /hooks/permission-request/{vendor} — that route runs
        // RunPermissionFlow which long-polls up to 10 hours waiting for a
        // hosted-agent UI decision. With Codex's 30 s hook timeout, the hook
        // process is killed long before the server returns (AI-636).
        //
        // Single 2 s deadline covers BOTH the /auth/config discovery inside
        // CreateAuthenticatedClientAsync and the /hooks/permission-record POST.
        // Without bounding discovery too, a server that accepts the TCP
        // connection but stalls on /auth/config can burn the full HttpClient
        // default (100 s) before we even start the POST, blowing past Codex's
        // 30 s hook timeout. Passing baseUrl also keeps discovery targeted at
        // the server we're about to POST to, not the
        // AppConfig.ResolvedServerUrl / KAPACITOR_URL / localhost:5108 fallback.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try {
            using var client  = await HttpClientExtensions.CreateAuthenticatedClientAsync(baseUrl, cts.Token);
            using var content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json");
            using var _       = await client.PostAsync($"{baseUrl}/hooks/permission-record", content, cts.Token);
        } catch {
            // Best-effort — recording must never block Codex's approval prompt.
        }

        // Empty hookSpecificOutput → Codex treats it as "no decision" and runs
        // its normal approval flow. See
        // codex-rs/hooks/src/events/permission_request.rs in openai/codex.
        Console.Write("{}");
        return 0;
    }

    static async Task<int> HandlePermissionRequestViaBridge(string daemonUrl, JsonNode node) {
        if (!DaemonBridgeUrl.TryParseLoopback(daemonUrl, out var bridgeBase)) {
            Console.Error.WriteLine(
                $"[kapacitor] codex-hook permission-request: KAPACITOR_DAEMON_URL must be http loopback, got: {daemonUrl}");
            return EmitDenyAndExitNonzero();
        }

        using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        try {
            using var content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp    = await client.PostAsync($"{bridgeBase}/codex/permission-request", content);

            if (!resp.IsSuccessStatusCode) {
                Console.Error.WriteLine(
                    $"[kapacitor] codex-hook permission-request bridge: HTTP {(int)resp.StatusCode}");
                return EmitDenyAndExitNonzero();
            }

            var body = await resp.Content.ReadAsStringAsync();
            Console.Write(body);
            return 0;
        } catch (Exception ex) {
            Console.Error.WriteLine($"[kapacitor] codex-hook permission-request bridge error: {ex.Message}");
            return EmitDenyAndExitNonzero();
        }
    }

    static int EmitDenyAndExitNonzero() {
        var response = new JsonObject {
            ["hookSpecificOutput"] = new JsonObject {
                ["hookEventName"] = "PermissionRequest",
                ["decision"]      = new JsonObject { ["behavior"] = "deny" }
            }
        };

        Console.Write(response.ToJsonString());
        return 1;
    }

    static async Task<int> PostHookAsync(string baseUrl, string endpoint, string body) {
        using var client  = await HttpClientExtensions.CreateAuthenticatedClientAsync();
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        try {
            var resp = await client.PostWithRetryAsync($"{baseUrl}/hooks/{endpoint}", content);
            if (!resp.IsSuccessStatusCode) {
                Console.Error.WriteLine($"[kapacitor] codex-hook {endpoint}: HTTP {(int)resp.StatusCode}");
                return 1;
            }

            return 0;
        } catch (HttpRequestException ex) {
            HttpClientExtensions.WriteUnreachableError(baseUrl, ex);
            return 1;
        }
    }

    static void NormalizeGuidField(JsonNode node, string fieldName) {
        var value = TryGetString(node, fieldName);

        if (value is not null && value.Contains('-')) {
            node[fieldName] = value.Replace("-", "");
        }
    }

    /// <summary>
    /// Safely extracts a string from <paramref name="node"/>[<paramref name="fieldName"/>].
    /// Returns null (instead of throwing) when the field is absent, null, or not a string.
    /// </summary>
    static string? TryGetString(JsonNode? node, string fieldName) {
        if (node?[fieldName] is JsonValue v && v.TryGetValue<string>(out var s)) {
            return s;
        }

        return null;
    }
}
