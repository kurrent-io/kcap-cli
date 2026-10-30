namespace Capacitor.Cli.Commands;

/// <summary>
/// What <c>kcap import</c> exits with once a run has finished: 0 when nothing it tried to send failed,
/// <see cref="Partial"/> when some sessions landed and others did not, 1 when none did. A script that
/// reads 0 can rely on every session having landed.
/// </summary>
static class ImportExitCode {
    internal const int Partial = 2;

    /// <param name="handled">What <c>HandleImport</c> returned, which is 0 for a run whose sessions
    /// failed.</param>
    /// <param name="outcome">The run's outcome, or null for a run that ended before it had one.</param>
    internal static int For(int handled, ImportCommand.ImportRunOutcome? outcome) {
        if (handled != 0 || outcome is not { AnythingFailed: true }) return handled;

        return outcome.Counts.Imported + outcome.Counts.AlreadyLoaded > 0 ? Partial : 1;
    }
}
