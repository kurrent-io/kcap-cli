namespace Capacitor.Cli.LocalEdition;

static class FinishedReports {
    /// <summary>Reports an operation whose journal is already cleared. It has finished whatever the sink does, so a
    /// sink that throws must not turn it into a failure.</summary>
    public static void ReportFinished(this IProgress<LocalInstallEvent> progress, LocalInstallEvent finished) {
        try {
            progress.Report(finished);
        } catch (Exception) {
            // The outcome is on disk; only its narration is lost.
        }
    }
}
