using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Kiro;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Single-binary dispatcher for AWS Kiro CLI hooks (AI-888). Kiro (the rebranded
/// Amazon Q Developer CLI) delivers each hook as JSON on STDIN; the kcap
/// installer writes one entry per event with the event name embedded in the
/// command: <c>kcap hook --kiro --event agentSpawn</c>.
/// </summary>
/// <remarks>
/// Wire contract (Kiro event → server route):
///   agentSpawn → POST /hooks/session-start/kiro, then ensure the watcher is
///                running (vendor=kiro). agentSpawn fires on EVERY prompt with
///                the SAME session id, so the server's deterministic lifecycle
///                event id collapses them to one SessionStarted and the
///                idempotent EnsureWatcherRunning is a no-op once live. The
///                watcher reads the SQLite conversation, materializes a flattened
///                transcript, streams it (vendor=kiro), and — because Kiro has NO
///                session-end hook — synthesizes /hooks/session-end/kiro when it
///                observes the kiro-cli process exit.
///   (any other) → no-op exit 0.
///
/// Kiro treats non-empty hook stdout as re-injection (a stdout-writing hook loops
/// the agent), so this dispatcher emits NOTHING on stdout — only stderr on error.
/// </remarks>
static class KiroHookCommand {
    public static async Task<int> Handle(string baseUrl, TextReader stdin, string[] args) {
        // The installer always passes --event; default to agentSpawn so a
        // hand-rolled hook entry without it still records.
        var eventName = GetArg(args, "--event") ?? "agentSpawn";

        var body = await stdin.ReadToEndAsync();

        JsonNode? node;
        try {
            node = JsonNode.Parse(body);
        } catch {
            // Best effort — never crash the host CLI on a malformed payload.
            return 0;
        }

        if (node is null) return 0;

        // agentSpawn is the only actionable event; anything else is a no-op that
        // MUST exit 0 with empty stdout.
        if (eventName != "agentSpawn") return 0;

        // Kiro's session id is the conversation UUID (dashed). Keep the dashed
        // form for the server payload (matches the transcript's conversation_id)
        // and the dashless form for local keys (watcher pid file / disable
        // markers), mirroring every other vendor dispatcher.
        var dashedSessionId = TryGetString(node, "session_id");
        if (string.IsNullOrEmpty(dashedSessionId)) return 0;
        if (!Guid.TryParse(dashedSessionId, out _)) return 0;

        var sessionId = dashedSessionId.Replace("-", "");

        // Mirror the Claude/Codex/Copilot disabled-session fast path: `kcap
        // disable` must stop every POST and watcher restart for the session.
        if (DisabledSessions.IsDisabled(sessionId)) return 0;

        var cwd           = TryGetString(node, "cwd");
        var activeProfile = await AppConfig.GetActiveProfileAsync();

        // Cheap string-prefix path exclusion runs on every firing; repo exclusion
        // runs once after enrichment, then marks the session disabled so later
        // agentSpawn firings take the fast path above.
        if (activeProfile?.ExcludedPaths is { Length: > 0 } excludedPaths
         && PathExclusion.IsExcluded(cwd, excludedPaths)) {
            return 0;
        }

        return await HandleAgentSpawn(baseUrl, node, dashedSessionId, sessionId, cwd, activeProfile);
    }

    static async Task<int> HandleAgentSpawn(
            string   baseUrl,
            JsonNode node,
            string   dashedSessionId,
            string   sessionId,
            string?  cwd,
            Profile? activeProfile
        ) {
        var forwarded = new JsonObject {
            ["hook_event_name"] = "agentSpawn",
            ["session_id"]      = dashedSessionId,
            ["home_dir"]        = PathHelpers.HomeDirectory
        };

        if (cwd is not null) forwarded["cwd"] = cwd;

        if (Environment.GetEnvironmentVariable("KCAP_AGENT_ID") is { } agentHostId) {
            forwarded["agent_host_id"] = agentHostId;
        }

        // Stamp default visibility BEFORE enrichment so it survives the
        // JsonString round-trip (same rationale as the Codex/Copilot dispatchers).
        // null lets the server fall back to org-repo visibility, which would
        // silently flip private-default users' Kiro sessions to org-visible.
        if (activeProfile?.DefaultVisibility is { } visibility) {
            forwarded["default_visibility"] = visibility;
        }

        var enriched = await RepositoryDetection.EnrichWithRepositoryInfo(forwarded.ToJsonString());

        if (activeProfile?.ExcludedRepos is { Length: > 0 } excludedRepos
         && await RepoExclusion.IsExcludedAsync(enriched, excludedRepos)) {
            DisabledSessions.Mark(sessionId);
            return 0;
        }

        var exit = await PostHookAsync(baseUrl, "session-start/kiro", enriched);
        if (exit != 0) return exit;

        // The watcher tails a kcap-owned file that it materializes from the
        // SQLite conversation (Kiro has no on-disk JSONL transcript). It also
        // owns session-end: GetCodingAgentPid() inside SpawnWatcher passes the
        // kiro-cli pid as --parent-pid, so the watcher POSTs session-end/kiro
        // when kiro-cli exits.
        var transcriptPath = KiroPaths.MaterializedTranscript(sessionId);

        await WatcherManager.EnsureWatcherRunning(
            baseUrl, sessionId, transcriptPath,
            agentId: null, sessionIdOverride: null, cwd: cwd,
            skipTitle: false, vendor: "kiro"
        );

        return 0;
    }

    static async Task<int> PostHookAsync(string baseUrl, string endpoint, string body) {
        using var client  = await HttpClientExtensions.CreateAuthenticatedClientAsync();
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        try {
            var resp = await client.PostWithRetryAsync($"{baseUrl}/hooks/{endpoint}", content);

            if (!resp.IsSuccessStatusCode) {
                Console.Error.WriteLine($"[kcap] kiro-hook {endpoint}: HTTP {(int)resp.StatusCode}");
                return 1;
            }

            return 0;
        } catch (HttpRequestException ex) {
            HttpClientExtensions.WriteUnreachableError(baseUrl, ex);
            return 1;
        }
    }

    static string? GetArg(string[] args, string flag) {
        var idx = Array.IndexOf(args, flag);

        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }

    static string? TryGetString(JsonNode? node, string fieldName) {
        if (node?[fieldName] is JsonValue v && v.TryGetValue<string>(out var s)) {
            return s;
        }

        return null;
    }
}
