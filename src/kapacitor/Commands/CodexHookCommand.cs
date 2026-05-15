using System.Text;
using System.Text.Json.Nodes;

namespace kapacitor.Commands;

/// <summary>
/// Single-binary dispatcher for Codex hooks. Codex invokes the same command
/// for every hook event with <c>hook_event_name</c> in the JSON payload, so
/// we collapse the six event handlers behind one CLI entry point rather than
/// minting one subcommand per event the way the Claude path does.
/// </summary>
/// <remarks>
/// Wire contract (Codex event → server route):
///   SessionStart      → POST /hooks/session-start/codex
///   Stop              → POST /hooks/session-end/codex (Codex has no separate session-end hook)
///   PermissionRequest → POST /hooks/permission-record (fire-and-forget; CLI emits no decision so
///                       Codex's normal in-CLI approval prompt takes over). The hosted-agent branch
///                       (KAPACITOR_DAEMON_URL set) lands in AI-68 and will bounce through the
///                       daemon's LocalPermissionBridge to wait on the user's UI decision.
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
        // the Claude hook path in Program.cs but without the disabled-session
        // and plan_content branches (those are Claude-specific).
        NormalizeGuidField(node, "session_id");

        node["home_dir"] = PathHelpers.HomeDirectory;

        var agentHostId = Environment.GetEnvironmentVariable("KAPACITOR_AGENT_ID");
        if (agentHostId is not null) {
            node["agent_host_id"] = agentHostId;
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
        var sessionId  = TryGetString(node, "session_id");
        var transcript = TryGetString(node, "transcript_path");

        // Codex Stop is the closest analog to Claude's session-end (Codex has
        // no separate session-end hook — see AI-67 spike). Kill the watcher
        // BEFORE posting so the transcript is fully drained before the server
        // computes session-end stats.
        if (sessionId is not null) {
            await WatcherManager.KillWatcher(sessionId);

            if (transcript is not null) {
                await WatcherManager.InlineDrainAsync(baseUrl, sessionId, transcript, agentId: null, vendor: "codex");
            }
        }

        // Note the URL: session-END/codex, not stop/codex. The CLI translates
        // Codex's hook event name into Capacitor's canonical hook vocabulary
        // before posting, so the server doesn't have to know about Codex's
        // missing session-end concept.
        var (exit, responseBody) = await PostHookWithResponseAsync(baseUrl, "session-end/codex", node.ToJsonString());

        if (exit != 0) return exit;

        // Mirror the Claude session-end path in Program.cs: if the server flags
        // generate_whats_done, spawn the summary generator as a detached process
        // with --codex so it goes through codex exec, matching the vendor that
        // produced the work being summarised.
        if (sessionId is not null && ShouldSpawnWhatsDone(responseBody)) {
            WatcherManager.SpawnWhatsDoneGenerator(baseUrl, sessionId, vendor: "codex");
        }

        Console.Write(SessionScopedOutputJson);
        return 0;
    }

    /// <summary>
    /// Parses the session-end response body and returns whether the server asked
    /// the CLI to generate a what's-done summary. Extracted so the response-parsing
    /// branch is testable without actually spawning a subprocess.
    /// </summary>
    internal static bool ShouldSpawnWhatsDone(string? responseBody) {
        if (responseBody is null) return false;

        try {
            var responseNode = JsonNode.Parse(responseBody);
            return responseNode?["generate_whats_done"]?.GetValue<bool>() == true;
        } catch {
            // Best effort — non-JSON or wrong-typed flag both fall through to "no spawn"
            return false;
        }
    }

    static async Task<int> HandlePermissionRequest(string baseUrl, JsonNode node) {
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
        // The hosted-agent branch (KAPACITOR_DAEMON_URL set) is intentionally
        // not wired here — it lands with the rest of the hosted-Codex stack
        // in AI-68 so the wire shape, daemon bridge, and tests change together.
        try {
            // Pass baseUrl into CreateAuthenticatedClientAsync so auth-discovery
            // targets the same server we're about to POST to, not the
            // AppConfig.ResolvedServerUrl / KAPACITOR_URL / localhost:5108 fallback
            // chain — the fallback can hang for the full HttpClient default
            // (100 s) when something on the dev machine accepts connections on
            // 5108 but never replies, blowing past Codex's 30 s hook timeout.
            using var client  = await HttpClientExtensions.CreateAuthenticatedClientAsync(baseUrl);
            using var content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json");
            using var cts     = new CancellationTokenSource(TimeSpan.FromSeconds(2));
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

    static async Task<int> PostHookAsync(string baseUrl, string endpoint, string body) {
        var (exit, _) = await PostHookWithResponseAsync(baseUrl, endpoint, body);
        return exit;
    }

    static async Task<(int Exit, string? ResponseBody)> PostHookWithResponseAsync(string baseUrl, string endpoint, string body) {
        using var client  = await HttpClientExtensions.CreateAuthenticatedClientAsync();
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        try {
            var resp = await client.PostWithRetryAsync($"{baseUrl}/hooks/{endpoint}", content);
            if (!resp.IsSuccessStatusCode) {
                Console.Error.WriteLine($"[kapacitor] codex-hook {endpoint}: HTTP {(int)resp.StatusCode}");
                return (1, null);
            }

            var responseBody = await resp.Content.ReadAsStringAsync();
            return (0, responseBody);
        } catch (HttpRequestException ex) {
            HttpClientExtensions.WriteUnreachableError(baseUrl, ex);
            return (1, null);
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
