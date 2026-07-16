using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Copilot;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Single-binary dispatcher for GitHub Copilot CLI hooks (AI-815). Copilot
/// command-hook payloads carry no uniform event-name field (sessionStart's
/// payload is just <c>{sessionId, timestamp, cwd, source, initialPrompt}</c>),
/// so the kcap hooks installer writes one entry per event with the event name
/// embedded in the command: <c>kcap hook --copilot --event sessionStart</c>.
/// </summary>
/// <remarks>
/// Wire contract (Copilot event → server route):
///   sessionStart → POST /hooks/session-start/copilot, then spawn the watcher
///                  tailing $COPILOT_HOME/session-state/{sid}/events.jsonl
///                  with vendor=copilot (the server's CopilotTranscriptNormalizer
///                  owns content; the hook owns lifecycle). Fires with
///                  source:"resume" on the same session id for --continue /
///                  --resume — the server's deterministic lifecycle event ids
///                  make the re-POST idempotent and the watcher resumes from
///                  the server watermark.
///   sessionEnd   → spawn the detached copilot-finalize drainer FIRST (AI-897:
///                  it must be created before — and outlive — the rest of the
///                  hook to capture the session.shutdown tail Copilot writes
///                  after the hook returns), then kill watcher + capped inline
///                  drain (mirrors Claude's AI-813 pre-drain cap), then POST
///                  /hooks/session-end/copilot.
///   agentStop    → no server POST. Fires at every turn end; used only to
///                  re-enliven a crashed watcher (mirrors Codex's Stop).
///   notification → best-effort forward to the Claude-shaped /hooks/notification
///                  (Copilot's payload already carries message / title /
///                  notification_type in the compatible shape).
/// Copilot treats hook stdout as optional, so this dispatcher emits nothing.
/// </remarks>
static class CopilotHookCommand {
    // Mirror of ClaudeHookCommand.PreHookDrainCap (AI-813): Copilot kills the
    // sessionEnd hook at its configured timeout (default 30s, but kcap.json
    // entries set 30 and users can lower it) — the drain must never starve the
    // session-end POST, or the session sticks "Active" forever.
    static readonly TimeSpan PreHookDrainCap = TimeSpan.FromSeconds(8);

    // Notification forwarding is telemetry — a stalled server must not block
    // Copilot's turn loop. Single budget covers auth discovery + POST.
    static readonly TimeSpan NotificationPostBudget = TimeSpan.FromSeconds(2);

    public static async Task<int> Handle(string baseUrl, TextReader stdin, string[] args) {
        var eventName = GetArg(args, "--event");

        if (string.IsNullOrWhiteSpace(eventName)) {
            Console.Error.WriteLine(
                "kcap hook --copilot requires --event <name> (the kcap hooks installer writes it; "
              + "re-run: kcap plugin install --copilot)");
            return 1;
        }

        var body = await stdin.ReadToEndAsync();

        JsonNode? node;

        try {
            node = JsonNode.Parse(body);
        } catch {
            // Best effort — never crash the host CLI on a malformed payload.
            return 0;
        }

        if (node is null) return 0;

        // Copilot payloads carry the dashed session uuid under camelCase
        // `sessionId`. Keep the dashed form for filesystem lookups (the
        // session-state dir name is dashed) and the dashless form for the
        // server (AgentSession-{dashless} stream convention shared by every
        // vendor dispatcher).
        var dashedSessionId = TryGetString(node, "sessionId");

        if (string.IsNullOrEmpty(dashedSessionId)) return 0;

        // Copilot session ids are UUIDs (the session-state dir name) — but
        // subagent-scoped hook firings reuse the spawning toolCallId as
        // sessionId (captured v1.0.61 agentStop: sessionId:"toolu_01…",
        // transcriptPath:""). Those are not sessions: routing them onward
        // would spawn idle watchers (pid files + SignalR registrations) keyed
        // on tool-call ids. Subagent activity is already inlined in the
        // parent session's transcript, so dropping these loses nothing.
        if (!Guid.TryParse(dashedSessionId, out _)) return 0;

        var sessionId = dashedSessionId.Replace("-", "");

        // Mirror the Claude/Codex disabled-session fast path: `kcap disable`
        // must stop every POST and watcher restart for the session.
        if (DisabledSessions.IsDisabled(sessionId)) {
            if (eventName == "sessionEnd") DisabledSessions.RemoveMarker(sessionId);
            return 0;
        }

        // AI-1357 Task 12: the cross-vendor backlog drain now runs centrally in Program.cs's
        // `case "hook":` before dispatch — no longer wired here (removes the double-wire).
        var spool = new HookSpool(PathHelpers.ConfigPath("spool"));

        var cwd           = TryGetString(node, "cwd");
        var activeProfile = await AppConfig.GetActiveProfileAsync();

        // Path exclusion is a cheap string-prefix compare — safe on every
        // event (agentStop fires per turn). Repo exclusion runs once inside
        // sessionStart after enrichment, then marks the session disabled so
        // later events take the fast path above (same split as Codex).
        if (activeProfile?.ExcludedPaths is { Length: > 0 } excludedPaths
         && PathExclusion.IsExcluded(cwd, excludedPaths)) {
            return 0;
        }

        return eventName switch {
            "sessionStart" => await HandleSessionStart(baseUrl, node, dashedSessionId, sessionId, cwd, activeProfile, spool),
            "sessionEnd"   => await HandleSessionEnd(baseUrl, node, dashedSessionId, sessionId, cwd),
            "agentStop"    => await HandleAgentStop(baseUrl, node, dashedSessionId, sessionId, cwd),
            "notification" => await HandleNotification(baseUrl, node, sessionId, cwd),
            _              => 0   // unknown — silently ignore (fail-open like the other dispatchers)
        };
    }

    static async Task<int> HandleSessionStart(
            string    baseUrl,
            JsonNode  node,
            string    dashedSessionId,
            string    sessionId,
            string?   cwd,
            Profile?  activeProfile,
            HookSpool spool
        ) {
        var source = TryGetString(node, "source") is { Length: > 0 } s ? s : "startup";

        var forwarded = new JsonObject {
            ["hook_event_name"] = "sessionStart",
            ["session_id"]      = sessionId,
            ["source"]          = source,
            ["home_dir"]        = PathHelpers.HomeDirectory
        };

        if (cwd is not null) {
            forwarded["cwd"] = cwd;

            // AI-701: best-effort git-root discovery, fail-open (omitted when no repo is found).
            if (GitRepository.FindRoot(cwd) is { } workspaceRoot) forwarded["workspace_root"] = workspaceRoot;
        }

        if (TryGetString(node, "initialPrompt") is { } prompt) {
            forwarded["initial_prompt"] = prompt;
        }

        // Copilot stamps hook payloads with a unix-ms timestamp; forward it as
        // started_at so canonical SessionStarted carries the real start time
        // (the server falls back to UtcNow when absent — AI-739 precedent).
        if (TryGetUnixMillis(node, "timestamp") is { } startedAt) {
            forwarded["started_at"] = startedAt.ToString("O");
        }

        if (Environment.GetEnvironmentVariable("KCAP_AGENT_ID") is { } agentHostId) {
            forwarded["agent_host_id"] = agentHostId;
        }

        // Same rationale as the Codex dispatcher: stamp default visibility
        // BEFORE enrichment so it survives the JsonString round-trip.
        if (activeProfile?.DefaultVisibility is { } visibility) {
            forwarded["default_visibility"] = visibility;
        }

        var enriched = await RepositoryDetection.EnrichWithRepositoryInfo(forwarded.ToJsonString());

        // Repo exclusion after enrichment (fast in-payload path) — mark the
        // session so per-turn agentStop events skip via DisabledSessions.
        if (activeProfile?.ExcludedRepos is { Length: > 0 } excludedRepos
         && await RepoExclusion.IsExcludedAsync(enriched, excludedRepos)) {
            DisabledSessions.Mark(sessionId);
            return 0;
        }

        // Spawn-before-post (AI-1357): capture must start on Posted OR Spooled (auth lapse /
        // outage) — a doomed/delayed lifecycle POST must never withhold the watcher. Only a
        // permanent failure keeps the prior non-zero exit and skips the watcher.
        var outcome = await AgentHookPoster.PostOrSpoolAsync(
            baseUrl, "session-start/copilot", enriched, "copilot-hook",
            spool, sessionId, route: "session-start/copilot");

        if (!AgentHookPoster.ShouldSpawnAfter(outcome)) return outcome == HookPostOutcome.Failed ? 1 : 0;

        await EnsureWatcherAsync(baseUrl, dashedSessionId, sessionId, node, cwd);
        return 0;
    }

    static async Task<int> HandleSessionEnd(
            string   baseUrl,
            JsonNode node,
            string   dashedSessionId,
            string   sessionId,
            string?  cwd
        ) {
        var transcriptPath = TranscriptPathFor(dashedSessionId);

        // AI-897: Copilot appends `session.shutdown` (per-model input/cache token
        // aggregates) — and sometimes the final assistant turn — to events.jsonl
        // only AFTER this hook returns, by which point the live watcher is dead
        // (KillWatcher below) and the server's session-end StopAndDrain has run,
        // so nothing else is reading the file. Spawn the detached finalizer FIRST,
        // before the capped pre-drain and the retrying session-end POST: if a
        // slow/unreachable server makes the POST burn the whole hook timeout,
        // Copilot SIGKILLs the hook — and we must have already created the drainer
        // by then. It is detached (setsid + closed std streams), so it survives
        // the hook being killed and still delivers the post-hook tail via one
        // idempotent inline-drain once `session.shutdown` lands (or it times out).
        // Its poll budget outlasts the worst-case hook lifetime for this reason.
        WatcherManager.SpawnCopilotFinalizeDrain(baseUrl, sessionId, transcriptPath);

        // Kill watcher + inline-drain BEFORE the POST so the server computes
        // stats over the full transcript — capped so a slow drain can't starve
        // the session-end POST (mirror of ClaudeHookCommand / AI-813).
        try {
            var drained = await TimeBudget.RunCappedAsync(
                async () => {
                    await WatcherManager.KillWatcher(sessionId);
                    await WatcherManager.InlineDrainAsync(baseUrl, sessionId, transcriptPath, agentId: null, vendor: "copilot");
                },
                PreHookDrainCap
            );

            if (!drained) {
                await Console.Error.WriteLineAsync(
                    $"[kcap] copilot session-end pre-drain cap ({PreHookDrainCap.TotalSeconds:0}s) elapsed; proceeding to POST. "
                  + $"Transcript tail may be incomplete — recoverable via: kcap import --copilot --session {sessionId}"
                );
            }
        } catch (Exception ex) {
            Console.Error.WriteLine($"[kcap] copilot session-end pre-hook failed: {ex.Message}");
        }

        var forwarded = new JsonObject {
            ["hook_event_name"] = "sessionEnd",
            ["session_id"]      = sessionId,
            ["reason"]          = TryGetString(node, "reason") ?? "complete",
            ["home_dir"]        = PathHelpers.HomeDirectory
        };

        if (cwd is not null) forwarded["cwd"] = cwd;

        if (TryGetUnixMillis(node, "timestamp") is { } endedAt) {
            forwarded["ended_at"] = endedAt.ToString("O");
        }

        if (Environment.GetEnvironmentVariable("KCAP_AGENT_ID") is { } agentHostId) {
            forwarded["agent_host_id"] = agentHostId;
        }

        // AuthLapsed / Posted → clean exit (0); a real failure keeps the prior non-zero exit.
        return await PostHookAsync(baseUrl, "session-end/copilot", forwarded.ToJsonString()) == HookPostOutcome.Failed ? 1 : 0;
    }

    static async Task<int> HandleAgentStop(string baseUrl, JsonNode node, string dashedSessionId, string sessionId, string? cwd) {
        // Fires at every turn end — no server POST (sessionEnd owns lifecycle;
        // turn content arrives via the transcript). Just keep the watcher
        // alive in case it crashed mid-session, mirroring Codex's Stop branch.
        _ = node;
        await EnsureWatcherAsync(baseUrl, dashedSessionId, sessionId, node, cwd);
        return 0;
    }

    static async Task<int> HandleNotification(string baseUrl, JsonNode node, string sessionId, string? cwd) {
        // The server's NotificationHook requires message + notification_type.
        // Copilot's command-hook stdin ships the Claude-compatible snake_case
        // key today (verified against captured v1.0.61 payloads), but its
        // internal event model uses camelCase `notificationType` (visible in
        // the transcript's hook.start input echo) — read both so a future
        // Copilot release dropping the compat transformation degrades to
        // "still recorded" instead of silently losing every notification.
        var message          = TryGetString(node, "message");
        var notificationType = TryGetString(node, "notification_type")
                            ?? TryGetString(node, "notificationType");

        if (message is null || notificationType is null) return 0;

        var forwarded = new JsonObject {
            ["hook_event_name"]   = "Notification",
            ["session_id"]        = sessionId,
            ["message"]           = message,
            ["notification_type"] = notificationType,
            ["home_dir"]          = PathHelpers.HomeDirectory
        };

        if (TryGetString(node, "title") is { } title) forwarded["title"] = title;
        if (cwd is not null) forwarded["cwd"] = cwd;

        // Best-effort telemetry — bounded like the Codex permission-record
        // path so a stalled server can't block Copilot's loop.
        using var cts = new CancellationTokenSource(NotificationPostBudget);
        try {
            // Status-returning variant (not CreateAuthenticatedClientAsync, which writes a
            // per-turn "expired" line to stderr): on a lapse, stay quiet and skip the doomed POST.
            var (client, status) = await HttpClientExtensions.CreateClientWithAuthStatusAsync(baseUrl, cts.Token);
            using (client) {
                if (AgentHookPoster.IsAuthLapsed(status)) return 0;
                using var content = new StringContent(forwarded.ToJsonString(), Encoding.UTF8, "application/json");
                using var _       = await client.PostAsync($"{baseUrl}/hooks/notification", content, cts.Token);
            }
        } catch {
            // Recording must never fail the hook.
        }

        return 0;
    }

    static async Task EnsureWatcherAsync(string baseUrl, string dashedSessionId, string sessionId, JsonNode node, string? cwd) {
        // agentStop payloads carry transcriptPath; sessionStart's don't —
        // derive it from the session-state layout in that case. Copilot also
        // ships transcriptPath as an EMPTY STRING on some firings, so treat
        // empty as absent rather than spawning a watcher on "".
        var transcriptPath = TryGetString(node, "transcriptPath") is { Length: > 0 } tp
            ? tp
            : TranscriptPathFor(dashedSessionId);

        await WatcherManager.EnsureWatcherRunning(
            baseUrl, sessionId, transcriptPath,
            agentId: null, sessionIdOverride: null, cwd: cwd,
            skipTitle: false, vendor: "copilot"
        );
    }

    /// <summary>
    /// events.jsonl path for a session. Prefers the current
    /// <c>session-state/</c> root; falls back to the pre-GA
    /// <c>history-session-state/</c> when only the legacy dir has the file.
    /// When neither exists yet (sessionStart can fire before Copilot's first
    /// event write) returns the current-layout path — the watcher tolerates a
    /// not-yet-created file and picks it up on its next poll.
    /// </summary>
    static string TranscriptPathFor(string dashedSessionId) {
        var current = CopilotPaths.EventsJsonl(CopilotPaths.SessionStateDir(), dashedSessionId);

        if (File.Exists(current)) return current;

        var legacy = CopilotPaths.EventsJsonl(CopilotPaths.LegacySessionStateDir(), dashedSessionId);

        return File.Exists(legacy) ? legacy : current;
    }

    // Shared auth-aware recording POST: skips the doomed POST (and the misleading per-turn
    // "HTTP 401" stderr line) when auth has lapsed, reporting AuthLapsed so the caller exits
    // cleanly instead of erroring. See AgentHookPoster.
    static Task<HookPostOutcome> PostHookAsync(string baseUrl, string endpoint, string body)
        => AgentHookPoster.PostAsync(baseUrl, endpoint, body, "copilot-hook");

    static string? GetArg(string[] args, string flag) {
        var idx = Array.IndexOf(args, flag);

        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }

    static DateTimeOffset? TryGetUnixMillis(JsonNode? node, string fieldName) {
        if (node?[fieldName] is not JsonValue v) return null;

        if (v.TryGetValue<long>(out var ms) && ms > 0) return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        if (v.TryGetValue<double>(out var dms) && dms > 0) return DateTimeOffset.FromUnixTimeMilliseconds((long)dms);

        return null;
    }

    static string? TryGetString(JsonNode? node, string fieldName) {
        if (node?[fieldName] is JsonValue v && v.TryGetValue<string>(out var s)) {
            return s;
        }

        return null;
    }
}
