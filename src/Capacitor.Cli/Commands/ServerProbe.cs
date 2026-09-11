using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;

using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Commands;

/// <summary>
/// One authenticated, read-only GET against <see cref="WhoamiCommand.ProbePath"/>: the server observes the
/// CLI's version header on it and answers with its own version, which the hook lane records in
/// <see cref="ServerVersionStore"/>. Fail-open, and bounded to <see cref="Budget"/> with discovery included.
/// </summary>
internal static class ServerProbe {
    static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    public static async Task SendAsync(CapacitorServer server, ICapacitorHttpClient http) {
        try {
            using var cts = new CancellationTokenSource(Budget);

            var (client, status) = await http.ForHookAsync(cts.Token);

            using (client) {
                // Reached with no server configured — every caller is an offline command — and the status
                // says so before a URL is needed.
                if (status is not (AuthStatus.Ok or AuthStatus.NoAuthRequired)) return;

                var url = AppConfig.NormalizeUrl(server.Url) + WhoamiCommand.ProbePath;

                using var _ = await client.GetOnceAsync(url, Budget, cts.Token);
            }
        } catch {
            // No caller can act on a failed probe.
        }
    }
}
