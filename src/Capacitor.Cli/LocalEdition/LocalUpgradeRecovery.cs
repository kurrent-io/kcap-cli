using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>Finishes an upgrade the previous lifecycle command left in the journal: before the switch it is undone
/// and the old version started again if it was running, from the switch on it is rolled forward. An entry this build
/// would not have written changes nothing.</summary>
sealed class LocalUpgradeRecovery(LocalStack stack, IProgress<LocalInstallEvent> progress) : ILifecycleRecovery {
    public string Operation => LocalUpgradeJournal.Operation;

    public async Task RecoverAsync(LifecycleLease lease, LifecycleJournalEntry interrupted, CancellationToken ct) {
        if (!LocalUpgradeJournal.TryRead(interrupted, out var details, out var error))
            throw new LocalLifecycleException(
                $"The upgrade journal {lease.Root.JournalPath} cannot be acted on ({error}). Nothing was changed. Fix or remove the journal. Run kcap local upgrade again.");

        progress.Report(new LocalInstallEvent.Recovering(Operation, interrupted.Phase));

        await new LocalUpgrade(stack, progress).ResumeAsync(lease, interrupted, details);

        progress.TryReport(new LocalInstallEvent.Recovered(Operation, interrupted.Phase));
    }
}
