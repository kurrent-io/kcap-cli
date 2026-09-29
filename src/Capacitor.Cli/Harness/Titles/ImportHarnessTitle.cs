using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Harness.Titles;

/// <summary>Posts an import-discovered harness title, riding out the window where the server has
/// accepted a session but not yet projected it (a coded <see cref="HarnessTitleOutcome.SessionNotFound"/>)
/// and any transient status within each request's budget.
/// Shared by every routed import source that reads a title from the harness's own store.</summary>
internal static class ImportHarnessTitle {
    // Sums to ~60s, the projection lag this rides out.
    static readonly TimeSpan[] Backoff = [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15),
    ];

    public static Task PostAsync(
            HttpClient                 client,
            TimeProvider                time,
            string                      baseUrl,
            string                      sessionId,
            HarnessTitlePost            post,
            IProgress<ImportProgress>?  progress,
            CancellationToken           ct
        ) => PostAsync(client, time, time, baseUrl, sessionId, post, progress, ct);

    /// <summary>Splits the backoff-delay clock from the one <see cref="HarnessTitleClient"/> hands its
    /// per-attempt HTTP timeout, so a test can drive the backoff clock without also racing that timeout —
    /// production passes the same provider for both, and the public overload above is what every caller
    /// outside this file uses.</summary>
    internal static async Task PostAsync(
            HttpClient                 client,
            TimeProvider                backoffTime,
            TimeProvider                httpTime,
            string                      baseUrl,
            string                      sessionId,
            HarnessTitlePost            post,
            IProgress<ImportProgress>?  progress,
            CancellationToken           ct
        ) {
        for (var attempt = 0; ; attempt++) {
            // Import has no later tick, so a transient status is ridden out here too.
            var outcome = await HarnessTitleClient.PostOrFallBackAsync(client, httpTime, baseUrl, sessionId, post, ct, retryStatuses: true);

            if (outcome != HarnessTitleOutcome.SessionNotFound) {
                if (outcome is HarnessTitleOutcome.Refused or HarnessTitleOutcome.Failed)
                    progress?.Report(new ImportTitleNotRecorded(sessionId, null, Reason(outcome)));

                return;
            }

            if (attempt >= Backoff.Length) {
                progress?.Report(new ImportTitleNotRecorded(sessionId, null, Reason(outcome)));

                return;
            }

            await Task.Delay(Backoff[attempt], backoffTime, ct);
        }
    }

    static string Reason(HarnessTitleOutcome outcome) => outcome switch {
        HarnessTitleOutcome.SessionNotFound => "session was never visible to the server",
        HarnessTitleOutcome.Refused         => "refused",
        _                                    => "request failed",
    };
}
