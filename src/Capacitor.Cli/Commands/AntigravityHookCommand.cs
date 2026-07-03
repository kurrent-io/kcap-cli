using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Dispatcher for Google Antigravity's control hooks (AI-1158). Antigravity is a GUI
/// IDE with no shell hooks; the kcap plugin (a block in Antigravity's <c>hooks.json</c>)
/// registers one command per lifecycle/tool event. Because the JSON payload carries NO
/// event-name field, the event is passed as a positional arg:
///   <c>kcap hook --antigravity &lt;Event&gt;</c>   with the payload on stdin.
///
/// Wire contract (mirrors <see cref="OpenCodeHookCommand"/> — Antigravity likewise has
/// the watcher own session content + session-end): only <c>PreInvocation</c> is
/// actionable — it POSTs /hooks/session-start/antigravity and ensures a watcher is
/// running (vendor=antigravity) tailing the conversation's <c>transcript_full.jsonl</c>.
/// PreInvocation re-fires cheaply on every turn; the server's deterministic lifecycle id
/// collapses the repeats and <see cref="WatcherManager.EnsureWatcherRunning"/> is a no-op
/// once live. Session-end is watcher-owned: Antigravity's IDE process outlives any one
/// conversation (like the Codex desktop), so the watcher self-terminates on idle and
/// POSTs /hooks/session-end/antigravity. <c>Stop</c>/<c>PostInvocation</c>/tool events
/// are no-ops here (the watcher already tails the transcript continuously).
///
/// Fail-open throughout — a kcap/server problem must never disrupt the Antigravity IDE.
/// Antigravity conversation ids are dashed UUIDs; the same raw form is used for BOTH the
/// session-start payload and the watcher so they resolve to one stream (no dash-strip).
/// </summary>
static class AntigravityHookCommand {
    public static Task<int> Handle(string baseUrl, string[] args) =>
        Handle(baseUrl, args, Console.In);

    internal static async Task<int> Handle(string baseUrl, string[] args, TextReader stdin) {
        var eventName = EventArg(args);
        if (string.IsNullOrWhiteSpace(eventName)) {
            Console.Error.WriteLine(
                "kcap hook --antigravity requires an event name, e.g. "
              + "`kcap hook --antigravity PreInvocation` (the kcap Antigravity plugin passes it; "
              + "re-run: kcap plugin install --antigravity)");
            return 1;
        }

        // PreInvocation is the only actionable event; the watcher owns everything else.
        if (eventName != "PreInvocation") return 0;

        JsonObject? payload;
        try {
            payload = JsonNode.Parse(await stdin.ReadToEndAsync()) as JsonObject;
        } catch {
            return 0; // malformed payload — fail open, next PreInvocation retries
        }
        if (payload is null) return 0;

        var conversationId = payload["conversationId"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(conversationId)) return 0;

        var transcriptPath = payload["transcriptPath"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(transcriptPath)) return 0; // nothing to tail

        var cwd = FirstWorkspacePath(payload);

        // Mirror the disabled-session fast path: `kcap disable` must stop every POST
        // and watcher restart for the session.
        if (DisabledSessions.IsDisabled(conversationId)) return 0;

        var activeProfile = await AppConfig.GetActiveProfileAsync();

        if (activeProfile?.ExcludedPaths is { Length: > 0 } excludedPaths
         && PathExclusion.IsExcluded(cwd, excludedPaths)) {
            return 0;
        }

        return await HandleSessionStart(
            baseUrl, conversationId, transcriptPath, cwd, payload, activeProfile);
    }

    static async Task<int> HandleSessionStart(
            string      baseUrl,
            string      conversationId,
            string      transcriptPath,
            string?     cwd,
            JsonObject  payload,
            Profile?    activeProfile
        ) {
        var forwarded = new JsonObject {
            ["hook_event_name"] = "sessionStart",
            ["session_id"]      = conversationId,
            ["home_dir"]        = PathHelpers.HomeDirectory,
            ["started_at"]      = DateTimeOffset.UtcNow.ToString("O")
        };

        if (cwd is not null) forwarded["cwd"] = cwd;
        if (payload["antigravityVersion"]?.GetValue<string>() is { } version)
            forwarded["antigravity_version"] = version;

        if (Environment.GetEnvironmentVariable("KCAP_AGENT_ID") is { } agentHostId)
            forwarded["agent_host_id"] = agentHostId;

        // Stamp default visibility BEFORE enrichment so it survives the JsonString
        // round-trip (same rationale as the OpenCode/Kiro dispatchers); null lets the
        // server fall back to org-repo visibility.
        if (activeProfile?.DefaultVisibility is { } visibility)
            forwarded["default_visibility"] = visibility;

        var enriched = await RepositoryDetection.EnrichWithRepositoryInfo(forwarded.ToJsonString());

        if (activeProfile?.ExcludedRepos is { Length: > 0 } excludedRepos
         && await RepoExclusion.IsExcludedAsync(enriched, excludedRepos)) {
            DisabledSessions.Mark(conversationId);
            return 0;
        }

        // Fail-open: a non-zero exit would surface as a failed hook; skip the watcher
        // this firing and let the next PreInvocation retry.
        var exit = await PostHookAsync(baseUrl, "session-start/antigravity", enriched);
        if (exit != 0) return 0;

        await WatcherManager.EnsureWatcherRunning(
            baseUrl, conversationId, transcriptPath,
            agentId: null, sessionIdOverride: null, cwd: cwd,
            skipTitle: false, vendor: "antigravity"
        );

        return 0;
    }

    /// <summary>The event name — the first positional token after <c>--antigravity</c>.</summary>
    internal static string? EventArg(string[] args) {
        var idx = Array.IndexOf(args, "--antigravity");
        if (idx < 0 || idx + 1 >= args.Length) return null;

        var next = args[idx + 1];
        return next.StartsWith('-') ? null : next;
    }

    static string? FirstWorkspacePath(JsonObject payload) {
        if (payload["workspacePaths"] is JsonArray { Count: > 0 } paths
         && paths[0]?.GetValue<string>() is { Length: > 0 } first) {
            return first;
        }
        // Fall back to a singular form if present.
        return payload["cwd"]?.GetValue<string>();
    }

    static async Task<int> PostHookAsync(string baseUrl, string endpoint, string body) {
        using var client  = await HttpClientExtensions.CreateAuthenticatedClientAsync();
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        try {
            var resp = await client.PostWithRetryAsync($"{baseUrl}/hooks/{endpoint}", content);

            if (!resp.IsSuccessStatusCode) {
                Console.Error.WriteLine($"[kcap] antigravity-hook {endpoint}: HTTP {(int)resp.StatusCode}");
                return 1;
            }

            return 0;
        } catch (HttpRequestException ex) {
            HttpClientExtensions.WriteUnreachableError(baseUrl, ex);
            return 1;
        }
    }
}
