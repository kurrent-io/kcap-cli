using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>Finishes a purge the previous lifecycle command left in the journal. A purge is never undone: from either
/// phase it runs on to the end, once no daemon or watcher records any more. The journal denies the server a start but
/// not them, so one may have started since the crash. With <paramref name="mayStop"/> they are stopped; without it,
/// one still alive leaves the journal for <c>kcap local uninstall --purge</c>, which may.</summary>
sealed class LocalPurgeRecovery(
    ILocalServerService service, UserHome home, IOutgoingRecorders recorders, bool mayStop, IProgress<LocalInstallEvent> progress)
    : ILifecycleRecovery {
    public string Operation => LocalPurgeJournal.Operation;

    public async Task RecoverAsync(LifecycleLease lease, LifecycleJournalEntry interrupted, CancellationToken ct) {
        progress.Report(new LocalInstallEvent.Recovering(Operation, interrupted.Phase));

        if (mayStop && !await recorders.StopAsync())
            throw new LocalLifecycleException("An interrupted purge cannot finish: a daemon or watcher could not be stopped. The purge journal is kept.");

        if (recorders.Live() is { Count: > 0 } live)
            throw new LocalLifecycleException(
                $"An interrupted purge cannot finish while {string.Join(", ", live)} still records. The purge journal is kept; run kcap local uninstall --purge to stop them and finish it.");

        await LocalPurge.FinishAsync(lease, interrupted, service, home, ct);

        progress.TryReport(new LocalInstallEvent.Recovered(Operation, interrupted.Phase));
    }
}
