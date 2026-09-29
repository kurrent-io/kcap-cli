using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Harness.Titles;

/// <summary>Posts an import-discovered harness title, riding out the window where the server has
/// accepted a session but not yet projected it (a coded <see cref="HarnessTitleOutcome.SessionNotFound"/>).
/// Shared by every routed import source that reads a title from the harness's own store.</summary>
internal static class ImportHarnessTitle {
    // Sums to ~60s, the projection lag this rides out.
    static readonly TimeSpan[] Backoff = [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15),
    ];

    public static async Task PostAsync(
            HttpClient                 client,
            TimeProvider                time,
            string                      baseUrl,
            string                      sessionId,
            HarnessTitlePost            post,
            IProgress<ImportProgress>?  progress,
            CancellationToken           ct
        ) {
        for (var attempt = 0; ; attempt++) {
            var outcome = await HarnessTitleClient.PostOrFallBackAsync(client, time, baseUrl, sessionId, post, ct);

            if (outcome != HarnessTitleOutcome.SessionNotFound) {
                if (outcome is HarnessTitleOutcome.Refused or HarnessTitleOutcome.Failed)
                    progress?.Report(new ImportTitleNotRecorded(sessionId, null, Reason(outcome)));

                return;
            }

            if (attempt >= Backoff.Length) {
                progress?.Report(new ImportTitleNotRecorded(sessionId, null, Reason(outcome)));

                return;
            }

            await Task.Delay(Backoff[attempt], time, ct);
        }
    }

    static string Reason(HarnessTitleOutcome outcome) => outcome switch {
        HarnessTitleOutcome.SessionNotFound => "session was never visible to the server",
        HarnessTitleOutcome.Refused         => "refused",
        _                                    => "request failed",
    };
}
