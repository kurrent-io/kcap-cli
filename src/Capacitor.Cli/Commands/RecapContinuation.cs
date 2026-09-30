using Capacitor.Cli.Continuation;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.Cli.Commands;

/// <summary>The takeover half of <c>kcap recap &lt;id&gt; --continue</c>. A refusal has its own exit code
/// so the caller can withhold the recap: an agent that reads a recap carries on with the work.</summary>
sealed class RecapContinuation(ConfigRoot config, ProfileContext profiles, TokenStore tokens, ICapacitorHttpClient http, TimeProvider time) {
    public const int Refused = 2;

    public async Task<int> RunAsync(string previous, bool force) {
        var baseUrl = profiles.Resolution.ServerUrl;

        if (baseUrl is null || !HttpClientExtensions.IsAcceptableUrl(baseUrl)) {
            await Console.Error.WriteLineAsync(HttpClientExtensions.SchemeMissingHint);
            return 1;
        }

        using var client = await http.ForCommandAsync();

        return await RunWithAsync(client, baseUrl, previous, WorkContextIds.CanonicalSessionId(HarnessRequesterContext.Resolve().SessionId), force);
    }

    internal async Task<int> RunWithAsync(HttpClient client, string baseUrl, string previous, string? current, bool force) {
        if (current is null) {
            await Console.Error.WriteLineAsync(
                "kcap recap --continue must run inside the session that takes over (CLAUDE_CODE_SESSION_ID, KCAP_SESSION_ID or CODEX_THREAD_ID).");
            return 1;
        }

        TakeoverResult result;

        try {
            result = await new SessionTakeover(AgentSessions.OnThisMachine(config), time).RunAsync(client, baseUrl, previous, current, force);
        } catch (HttpRequestException ex) {
            await Console.Error.WriteLineAsync($"Continuing session {previous} failed: {ex.Message}");
            return 1;
        }

        switch (result) {
            case TakeoverResult.Completed c:
                await Console.Out.WriteLineAsync(TakeoverReport.Render(c.Outcome));
                return c.AllWritesFailed ? 1 : 0;
            case TakeoverResult.Refused r:
                await Console.Error.WriteLineAsync(r.Reason);
                return Refused;
            case TakeoverResult.Failed f:
                await Console.Error.WriteLineAsync(f.Reason);
                return 1;
            default:
                await Console.Error.WriteLineAsync(await AuthRejectionNotice.ForPersistentUnauthorizedAsync(tokens, profiles.Name, baseUrl, time));
                return 1;
        }
    }
}
