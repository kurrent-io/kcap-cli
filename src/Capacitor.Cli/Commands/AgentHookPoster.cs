using System.Text;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Outcome of an agent-hook recording POST (<see cref="AgentHookPoster.PostAsync(string,string,string,string)"/>).
/// </summary>
internal enum HookPostOutcome {
    /// <summary>Auth was usable and the POST completed successfully.</summary>
    Posted,

    /// <summary>
    /// Auth has lapsed (expired refresh credential, or never logged in) — the POST was SKIPPED
    /// because the server would 401. No request was sent and nothing was written to stderr. The
    /// caller should skip follow-on work that also needs auth (e.g. spawning the transcript
    /// watcher) and exit cleanly, so a lapsed session produces no per-turn error banner.
    /// </summary>
    AuthLapsed,

    /// <summary>
    /// Auth was usable but the POST failed (non-success status or the server was unreachable).
    /// An error was already written to stderr; the caller keeps its existing failure handling.
    /// </summary>
    Failed,

    /// <summary>The POST could not be delivered now (auth lapsed or a transient/unreachable failure) so
    /// the payload was durably spooled for a later drain pass. NOT delivered — but the caller should still
    /// proceed to spawn the watcher (spawn-before-post): capture must not depend on lifecycle delivery.</summary>
    Spooled
}

/// <summary>
/// Shared recording-hook POST for the non-Claude agent hooks (Codex, Gemini, Copilot, Pi, Kiro,
/// OpenCode), which all otherwise built their client with <c>CreateAuthenticatedClientAsync</c>
/// and POSTed blindly. When auth has lapsed that meant a guaranteed-to-401 POST plus a
/// misleading per-turn <c>HTTP 401</c> stderr line; this helper instead reports
/// <see cref="HookPostOutcome.AuthLapsed"/> so the caller can skip the doomed work and exit
/// cleanly — carrying the Claude hook's #183 behaviour to the other hooks (AI-993).
///
/// These agents have no user-facing stdout notice channel (stdout is either unused or a JSON
/// decision/context channel), so no re-login nudge is surfaced here — the expired state is
/// visible via <c>kcap status</c> and the interactive CLI. A no-op for the <c>None</c> provider
/// (posts normally, unauthenticated) and unchanged when authenticated.
/// </summary>
internal static class AgentHookPoster {
    /// <summary>Auth has genuinely lapsed → any POST would 401. <c>Ok</c> and <c>NoAuthRequired</c> are usable.</summary>
    public static bool IsAuthLapsed(AuthStatus status) => status is AuthStatus.Expired or AuthStatus.NotAuthenticated;

    /// <summary>
    /// Builds an auth-aware client for <paramref name="baseUrl"/> and POSTs <paramref name="body"/>
    /// to <c>{baseUrl}/hooks/{endpoint}</c>, skipping the POST when auth has lapsed.
    /// <paramref name="agentTag"/> is the stderr prefix on a real failure, e.g. <c>"codex-hook"</c>.
    /// </summary>
    public static Task<HookPostOutcome> PostAsync(string baseUrl, string endpoint, string body, string agentTag)
        => PostAsync(() => HttpClientExtensions.CreateClientWithAuthStatusAsync(baseUrl), baseUrl, endpoint, body, agentTag);

    /// <summary>
    /// Core with an injectable <paramref name="clientFactory"/> (test seam — lets tests control the
    /// auth outcome without a token store or /auth/config discovery).
    /// </summary>
    internal static async Task<HookPostOutcome> PostAsync(
            Func<Task<(HttpClient Client, AuthStatus Status)>> clientFactory,
            string                                             baseUrl,
            string                                             endpoint,
            string                                             body,
            string                                             agentTag
        ) {
        var (client, status) = await clientFactory();

        using (client) {
            // Auth lapsed: the POST would 401. Skip it and report so the caller exits cleanly
            // (no per-turn stderr line / error banner); kcap status reports the expired state.
            if (IsAuthLapsed(status)) {
                return HookPostOutcome.AuthLapsed;
            }

            using var content = new StringContent(body, Encoding.UTF8, "application/json");

            try {
                using var resp = await client.PostWithRetryAsync($"{baseUrl}/hooks/{endpoint}", content);

                if (!resp.IsSuccessStatusCode) {
                    Console.Error.WriteLine($"[kcap] {agentTag} {endpoint}: HTTP {(int)resp.StatusCode}");
                    return HookPostOutcome.Failed;
                }

                return HookPostOutcome.Posted;
            } catch (HttpRequestException ex) {
                HttpClientExtensions.WriteUnreachableError(baseUrl, ex);
                return HookPostOutcome.Failed;
            }
        }
    }

    /// <summary>
    /// AI-1357: spawn-before-post variant. Like <see cref="PostAsync(string,string,string,string)"/>,
    /// but on a lapsed-auth or transient (5xx/408/429/unreachable) failure it durably spools the
    /// lifecycle payload to <paramref name="spool"/> and returns <see cref="HookPostOutcome.Spooled"/>
    /// (a global drain pass replays it after recovery). Callers treat <c>Posted</c> OR <c>Spooled</c>
    /// as "proceed to spawn the watcher"; never <c>Spooled</c> as delivered.
    /// </summary>
    public static Task<HookPostOutcome> PostOrSpoolAsync(
            string baseUrl, string endpoint, string body, string agentTag,
            HookSpool spool, string sessionId, string route)
        => PostOrSpoolAsync(() => HttpClientExtensions.CreateClientWithAuthStatusAsync(baseUrl),
                            baseUrl, endpoint, body, agentTag, spool, sessionId, route);

    /// <summary>
    /// AI-1357: spawn-before-post decision. Capture must start regardless of whether the lifecycle
    /// POST was actually delivered — <c>Posted</c> and <c>Spooled</c> both proceed to
    /// <c>WatcherManager.EnsureWatcherRunning</c>. <c>AuthLapsed</c> is kept here (spawning) as a
    /// defensive fallback for any caller still on the legacy <see cref="PostAsync(string,string,string,string)"/>
    /// that hasn't migrated to <see cref="PostOrSpoolAsync(string,string,string,string,HookSpool,string,string)"/>
    /// — the whole point of AI-1357 is capture-regardless-of-POST. Only a permanent <c>Failed</c>
    /// (a real non-2xx response, or auth lapsed on the legacy path having already been excluded)
    /// skips the watcher.
    /// </summary>
    public static bool ShouldSpawnAfter(HookPostOutcome outcome) =>
        outcome is HookPostOutcome.Posted or HookPostOutcome.Spooled or HookPostOutcome.AuthLapsed;

    /// <summary>
    /// AI-1357 Task 4: bounded, best-effort drain of the cross-vendor lifecycle + transcript spools.
    /// Run early in a JSON-payload vendor dispatcher's <c>Handle</c> (after the disabled-session fast
    /// path) so a prior session's backlog replays even when the current firing posts nothing further.
    /// Skips the drain entirely when auth has lapsed — a POST attempted with no bearer token would
    /// 401, and <see cref="LifecycleSpoolDrain"/>'s production poster treats a non-timeout/non-5xx
    /// status as a permanent drop, which would silently discard the very backlog this protects.
    /// Never throws — a spool-drain hiccup must not disrupt the vendor's own hook.
    /// </summary>
    public static async Task DrainSpoolsAsync(
            string baseUrl, HookSpool lifecycle, TranscriptSpool transcript, string? sessionId) {
        var budget = TimeSpan.FromSeconds(1.5);

        try {
            using var cts = new CancellationTokenSource(budget);
            var (client, status) = await HttpClientExtensions.CreateClientWithAuthStatusAsync(baseUrl, cts.Token);

            using (client) {
                if (IsAuthLapsed(status)) return;

                await LifecycleSpoolDrain.RunAsync(client, baseUrl, lifecycle, transcript, sessionId, budget, cts.Token);
            }
        } catch {
            // Best-effort — a drain hiccup must never disrupt the vendor's own hook.
        }
    }

    /// <summary>Core with an injectable client factory (test seam).</summary>
    internal static async Task<HookPostOutcome> PostOrSpoolAsync(
            Func<Task<(HttpClient Client, AuthStatus Status)>> clientFactory,
            string baseUrl, string endpoint, string body, string agentTag,
            HookSpool spool, string sessionId, string route) {
        var (client, status) = await clientFactory();

        using (client) {
            // Auth lapsed → the POST would 401. Spool for replay after `kcap login`; caller still spawns.
            if (IsAuthLapsed(status)) {
                spool.Append(sessionId, route, body);

                return HookPostOutcome.Spooled;
            }

            using var content = new StringContent(body, Encoding.UTF8, "application/json");

            try {
                using var resp = await client.PostWithRetryAsync($"{baseUrl}/hooks/{endpoint}", content);

                if (resp.IsSuccessStatusCode) {
                    return HookPostOutcome.Posted;
                }

                var code = (int)resp.StatusCode;

                // Transient (server down / rate-limit) → spool for retry; a permanent 4xx is a real failure.
                if (code is >= 500 or 408 or 429) {
                    spool.Append(sessionId, route, body);

                    return HookPostOutcome.Spooled;
                }

                Console.Error.WriteLine($"[kcap] {agentTag} {endpoint}: HTTP {code}");

                return HookPostOutcome.Failed;
            } catch (HttpRequestException) {
                // Unreachable after retries → transient; spool for a later drain rather than lose it.
                spool.Append(sessionId, route, body);

                return HookPostOutcome.Spooled;
            }
        }
    }
}
