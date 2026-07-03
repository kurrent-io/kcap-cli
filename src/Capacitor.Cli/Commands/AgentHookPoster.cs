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
    Failed
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
                var resp = await client.PostWithRetryAsync($"{baseUrl}/hooks/{endpoint}", content);

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
}
