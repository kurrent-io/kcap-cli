using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>Finishes a purge the previous lifecycle command left in the journal. A purge is never undone: from either
/// phase it runs on to the end.</summary>
sealed class LocalPurgeRecovery(ILocalServerService service, UserHome home, IProgress<LocalInstallEvent> progress) : ILifecycleRecovery {
    public string Operation => LocalPurgeJournal.Operation;

    public async Task RecoverAsync(LifecycleLease lease, LifecycleJournalEntry interrupted, CancellationToken ct) {
        progress.Report(new LocalInstallEvent.Recovering(Operation, interrupted.Phase));

        await LocalPurge.FinishAsync(lease, interrupted, service, home, ct);

        progress.TryReport(new LocalInstallEvent.Recovered(Operation, interrupted.Phase));
    }
}
