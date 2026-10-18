namespace Capacitor.Cli.LocalEdition;

static class ProgressReports {
    /// <summary>Reports an event whose narration must not change the outcome: a finished operation, or a rollback
    /// that is already under way. A sink that throws loses the event, nothing else.</summary>
    public static void TryReport(this IProgress<LocalInstallEvent> progress, LocalInstallEvent e) {
        try {
            progress.Report(e);
        } catch (Exception) {
            // What happened is on disk; only its narration is lost.
        }
    }
}
