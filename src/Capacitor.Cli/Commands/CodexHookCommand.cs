using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
// ReSharper disable ShortLivedHttpClient

namespace Capacitor.Cli.Commands;

/// <summary>
/// Single-binary dispatcher for Codex hooks. Codex invokes the same command
/// for every hook event with <c>hook_event_name</c> in the JSON payload, so
/// we collapse the six event handlers behind one CLI entry point rather than
/// minting one subcommand per event the way the Claude path does.
/// </summary>
/// <remarks>
/// Wire contract (Codex event → server route):
///   SessionStart      → POST /hooks/session-start/codex
///   Stop              → POST /hooks/stop (best-effort, 2s cap, swallow-all).
///                       Codex fires Stop at every turn end, not session end
///                       (AI-648); session-end stays owned by the watcher's
///                       parent-PID monitor (AI-647). The POST lets the server
///                       emit the idle-wait marker that clears the chat
///                       "working" indicator. HandleStop also refreshes watcher
///                       liveness and emits {"continue":true} for Codex's parser.
///   PermissionRequest → in a daemon-launched hosted agent (KCAP_DAEMON_URL set), bounce
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

        // KCAP_SKIP=1 marks a kcap-launched headless Codex invocation
        // (CodexCliRunner sets it). Suppress all server / watcher / git
        // enrichment work so we don't forward the nested session's hooks
        // back into kcap, but still honour Codex's stdout contract — the
        // Stop / SessionStart parsers reject empty output, and a missing
        // PermissionRequest response leaves Codex hung.
        if (Environment.GetEnvironmentVariable("KCAP_SKIP") is "1") {
            switch (eventName) {
                case "SessionStart" or "Stop":
                    Console.Write(SessionScopedOutputJson);
                    break;
                case "PermissionRequest":
                    // Empty hookSpecificOutput → Codex falls back to its
                    // own approval prompt. See HandlePermissionRequestStub.
                    Console.Write("{}");
                    break;
            }
            return 0;
        }

        // Normalize session_id to dashless GUID, inject home_dir, and tag the
        // agent host id when running inside a daemon-spawned agent. Mirrors
        // the Claude hook path in Program.cs (including the disabled-session
        // check below); the plan_content branch remains Claude-specific.
        NormalizeGuidField(node, "session_id");

        node["home_dir"] = PathHelpers.HomeDirectory;

        var agentHostId = Environment.GetEnvironmentVariable("KCAP_AGENT_ID");
        if (agentHostId is not null) {
            node["agent_host_id"] = agentHostId;
        }

        // Mirror the Claude path: if the user ran `kcap disable`, skip every
        // server POST and the watcher restart. Without this check the next Codex
        // Stop hook would re-enliven the watcher and re-send transcript data for
        // a session whose data was just deleted server-side.
        var disabledSessionId = TryGetString(node, "session_id");
        if (disabledSessionId is not null && DisabledSessions.IsDisabled(disabledSessionId)) {
            // Emit the session-scoped JSON Codex's Stop/SessionStart parsers expect,
            // then skip dispatch. (Claude's disabled branch also returns immediately
            // — see Program.cs around line 593.)
            if (eventName is "Stop" or "SessionStart") {
                Console.Write(SessionScopedOutputJson);
            }
            return 0;
        }

        // Path exclusion is a string-prefix compare against the payload's cwd
        // — cheap, safe to run on every event (including Stop, which fires
        // per turn). Repo exclusion is handled inside HandleSessionStart
        // instead: running it here would call RepoExclusion.IsExcludedAsync,
        // which falls back to DetectRepositoryAsync (multiple git commands +
        // gh pr view) when the payload lacks a repository block — too
        // expensive for the per-turn Stop hook. Doing the repo check once at
        // SessionStart (after enrichment, when the repository block is
        // populated) and marking the session via DisabledSessions lets
        // subsequent events take the existing disabled-session fast path
        // above without paying any git cost.
        var activeProfile = await AppConfig.GetActiveProfileAsync();

        if (activeProfile?.ExcludedPaths is { Length: > 0 } excludedPaths
         && PathExclusion.IsExcluded(TryGetString(node, "cwd"), excludedPaths)) {
            EmitFallbackOutput(eventName);
            return 0;
        }

        try {
            return eventName switch {
                "SessionStart"      => await HandleSessionStart(baseUrl, node, activeProfile),
                "Stop"              => await HandleStop(baseUrl, node),
                "PermissionRequest" => await HandlePermissionRequest(baseUrl, node),
                "UserPromptSubmit"
                  or "PreToolUse"
                  or "PostToolUse"  => 0,  // v1: swallow informational events
                _                   => 0   // unknown — silently ignore
            };
        } catch (Exception ex) {
            // Fail open, but NOT by leaving stdout empty: unlike Claude, Codex's
            // SessionStart/Stop parser rejects empty output and a missing
            // PermissionRequest response hangs it. If a handler throws (e.g. an
            // IO/permission fault while building the authenticated client), the
            // CLI's top-level guard would exit 0 with empty stdout and Codex would
            // report "invalid hook output" (AI-1168 review). Emit the
            // event-appropriate fallback here first, and record for diagnosis.
            CrashReporter.Record("hook", ex);
            EmitFallbackOutput(eventName);
            return 0;
        }
    }

    // Emit the minimal output Codex's parser requires for the given event — the
    // session-scoped {"continue":true} for SessionStart/Stop, an empty object for
    // PermissionRequest (so Codex's own approval prompt takes over), nothing for
    // the swallowed informational events. Used both when we intentionally skip
    // work (exclusion) and as the fail-open fallback when a handler throws.
    static void EmitFallbackOutput(string eventName) {
        switch (eventName) {
            case "SessionStart" or "Stop":
                // Codex's SessionStart/Stop parser rejects empty stdout.
                Console.Write(SessionScopedOutputJson);
                break;
            case "PermissionRequest":
                // Empty hookSpecificOutput → Codex's local approval prompt
                // takes over (matches the KCAP_SKIP=1 branch above).
                Console.Write("{}");
                break;
        }
    }

    static async Task<int> HandleSessionStart(string baseUrl, JsonNode node, Profile? activeProfile) {
        // Stamp the user's configured default visibility onto the payload
        // BEFORE git enrichment so it survives the JsonString round-trip.
        // /hooks/session-start/codex shares SessionStartHook with the Claude
        // route and the server-side SessionHookHandlers.HandleSessionStart
        // reads hook.DefaultVisibility for both vendors; without this, codex
        // sessions in org repos silently default to org-visible because
        // VisibilityService treats null as "fall back to org visibility".
        if (activeProfile?.DefaultVisibility is { } visibility) {
            node["default_visibility"] = visibility;
        }

        var enriched = await RepositoryDetection.EnrichWithRepositoryInfo(node.ToJsonString());

        // Repo exclusion runs here (not above the event switch) so that the
        // repository block is already populated by enrichment — RepoExclusion
        // takes the fast in-payload path and skips the expensive
        // DetectRepositoryAsync fallback. Mark the session via
        // DisabledSessions so subsequent Stop / PermissionRequest events
        // take the existing disabled-session fast path at the top of Handle
        // without paying any git cost.
        if (activeProfile?.ExcludedRepos is { Length: > 0 } excludedRepos
         && await RepoExclusion.IsExcludedAsync(enriched, excludedRepos)) {
            var excludedSessionId = TryGetString(node, "session_id");

            if (excludedSessionId is not null) DisabledSessions.Mark(excludedSessionId);

            Console.Write(SessionScopedOutputJson);
            return 0;
        }

        var outcome = await PostHookAsync(baseUrl, "session-start/codex", enriched);
        if (outcome == HookPostOutcome.Failed) return 1;

        // Emit Codex's required JSON output BEFORE spawning the watcher. The
        // watcher is best-effort and may take time to start; the parent (Codex)
        // is waiting on stdout, and there's nothing the watcher can contribute
        // to this response.
        Console.Write(SessionScopedOutputJson);

        // Auth lapsed: recording is paused until the user re-runs `kcap login`. We've satisfied
        // Codex's stdout contract and exit cleanly; skip the watcher — its POSTs would 401 too.
        if (outcome == HookPostOutcome.AuthLapsed) return 0;

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
        // We keep the watcher alive (in case it crashed mid-session) AND
        // best-effort POST /hooks/stop so the server can emit the idle-wait
        // marker that clears the chat "working" indicator (symmetric with
        // Claude's stop hook).
        var sessionId  = TryGetString(node, "session_id");
        var transcript = TryGetString(node, "transcript_path");
        var cwd        = TryGetString(node, "cwd");

        if (sessionId is not null && transcript is not null) {
            await WatcherManager.EnsureWatcherRunning(
                baseUrl, sessionId, transcript,
                agentId: null, sessionIdOverride: null, cwd: cwd,
                skipTitle: false, vendor: "codex"
            );

            await PostBestEffortAsync(baseUrl, "stop", node, TimeSpan.FromSeconds(2));
        }

        // AI-635: Codex's stop-hook output parser rejects empty stdout as
        // "invalid stop hook JSON output". Emit the schema default explicitly.
        Console.Write(SessionScopedOutputJson);
        return 0;
    }

    static async Task<int> HandlePermissionRequest(string baseUrl, JsonNode node) {
        var daemonUrl = Environment.GetEnvironmentVariable("KCAP_DAEMON_URL");

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
        // Single 2 s deadline covers BOTH the /auth/config discovery and the
        // /hooks/permission-record POST inside PostBestEffortAsync.
        // Without bounding discovery too, a server that accepts the TCP
        // connection but stalls on /auth/config can burn the full HttpClient
        // default (100 s) before we even start the POST, blowing past Codex's
        // 30 s hook timeout. Passing baseUrl also keeps discovery targeted at
        // the server we're about to POST to, not the
        // AppConfig.ResolvedServerUrl / KCAP_URL / localhost:5108 fallback.
        // Recording must never block Codex's approval prompt — see
        // PostBestEffortAsync for the shared swallow-all/cap behavior.
        await PostBestEffortAsync(baseUrl, "permission-record", node, TimeSpan.FromSeconds(2));

        // Empty hookSpecificOutput → Codex treats it as "no decision" and runs
        // its normal approval flow. See
        // codex-rs/hooks/src/events/permission_request.rs in openai/codex.
        Console.Write("{}");
        return 0;
    }

    static async Task<int> HandlePermissionRequestViaBridge(string daemonUrl, JsonNode node) {
        if (!DaemonBridgeUrl.TryParseLoopback(daemonUrl, out var bridgeBase)) {
            Console.Error.WriteLine(
                $"[kcap] codex-hook permission-request: KCAP_DAEMON_URL must be http loopback, got: {daemonUrl}");
            return EmitDenyAndExitNonzero();
        }

        using var client = new HttpClient();
        client.Timeout = Timeout.InfiniteTimeSpan;

        try {
            using var content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp    = await client.PostAsync($"{bridgeBase}/codex/permission-request", content);

            if (!resp.IsSuccessStatusCode) {
                Console.Error.WriteLine(
                    $"[kcap] codex-hook permission-request bridge: HTTP {(int)resp.StatusCode}");
                return EmitDenyAndExitNonzero();
            }

            var body = await resp.Content.ReadAsStringAsync();
            Console.Write(body);
            return 0;
        } catch (Exception ex) {
            Console.Error.WriteLine($"[kcap] codex-hook permission-request bridge error: {ex.Message}");
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

    // Shared auth-aware recording POST: skips the doomed POST (and the misleading per-turn
    // "HTTP 401" stderr line) when auth has lapsed, reporting AuthLapsed so the caller exits
    // cleanly instead of erroring. See AgentHookPoster.
    static Task<HookPostOutcome> PostHookAsync(string baseUrl, string endpoint, string body)
        => AgentHookPoster.PostAsync(baseUrl, endpoint, body, "codex-hook");

    /// <summary>
    /// Best-effort POST of <paramref name="node"/> to <c>/hooks/{endpoint}</c>, capped at
    /// <paramref name="cap"/> and swallowing every failure — it must never block, throw, or
    /// terminate the caller. The single deadline covers both /auth/config discovery and the POST.
    /// Callers that must satisfy Codex's stdout contract write their JSON output AFTER awaiting this.
    /// </summary>
    static async Task PostBestEffortAsync(string baseUrl, string endpoint, JsonNode node, TimeSpan cap) {
        // A blank or non-absolute baseUrl would trip EnsureAbsolute deep inside auth discovery,
        // which calls Environment.Exit(2) — uncatchable, and would bypass the caller's stdout/exit
        // contract. Bail silently before we can reach it.
        if (string.IsNullOrWhiteSpace(baseUrl) || !HttpClientExtensions.IsAcceptableUrl(baseUrl)) {
            return;
        }

        using var cts = new CancellationTokenSource(cap);

        try {
            // Use the status-returning variant, NOT CreateAuthenticatedClientAsync: the latter writes
            // "Not authenticated" / "expired" to stderr, which a per-turn Stop would spam. Stay quiet
            // and skip the POST when there's no usable auth (still swallow-all).
            var (client, status) = await HttpClientExtensions.CreateClientWithAuthStatusAsync(baseUrl, cts.Token);

            using (client) {
                if (status is not (AuthStatus.Ok or AuthStatus.NoAuthRequired)) {
                    return;
                }

                using var content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json");
                using var _       = await client.PostAsync($"{baseUrl}/hooks/{endpoint}", content, cts.Token);
            }
        } catch {
            // Best-effort — must never block or fail the caller.
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
