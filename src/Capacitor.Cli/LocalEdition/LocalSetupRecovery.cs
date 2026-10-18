using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>
/// Finishes or undoes a setup the previous lifecycle command left in the journal: before <c>committing</c> it is
/// rolled back, from <c>committing</c> on it is rolled forward with the journal's own details, never the current
/// process's. An entry this build would not have written changes nothing.
/// </summary>
sealed class LocalSetupRecovery(ILocalServerService service, UserHome home, HttpClient local, TimeProvider time, IProgress<LocalInstallEvent> progress)
    : ILifecycleRecovery {
    static readonly TimeSpan UnitLockWait = TimeSpan.FromMinutes(1);

    public string Operation => LocalSetupJournal.Operation;

    public async Task RecoverAsync(LifecycleLease lease, LifecycleJournalEntry interrupted, CancellationToken ct) {
        var root = lease.Root;

        if (interrupted.Phase is not (LocalSetupJournal.Pending or LocalSetupJournal.Unit or LocalSetupJournal.Committing or LocalSetupJournal.Allowed))
            throw Failed($"phase: \"{interrupted.Phase}\" needs a newer kcap to finish. The setup journal {root.JournalPath} was left as it is; update kcap, then run kcap setup --local again.");

        if (!LocalSetupJournal.TryRead(interrupted, out var details, out var error))
            throw Failed($"{error}. The setup journal {root.JournalPath} was not written by this kcap and was left as it is, with nothing changed; fix or remove it, then run kcap setup --local again.");

        progress.Report(new LocalInstallEvent.Recovering(Operation, interrupted.Phase));

        if (interrupted.Phase is LocalSetupJournal.Pending or LocalSetupJournal.Unit)
            await LocalSetupRollback.RunAsync(lease, interrupted, service, home, progress, "An earlier kcap setup --local stopped before it committed.");
        else
            await RollForwardAsync(lease, interrupted, details);

        progress.ReportFinished(new LocalInstallEvent.Recovered(Operation, interrupted.Phase));
    }

    async Task RollForwardAsync(LifecycleLease lease, LifecycleJournalEntry entry, SetupDetails details) {
        var unit = LocalServerUnit.ForInstallation(lease.Root, details.Port);

        if (!File.Exists(unit.ServerBinary))
            throw Failed($"The interrupted setup cannot be finished: {unit.ServerBinary} is missing. The setup journal {lease.Root.JournalPath} is kept.");

        if (entry.Phase == LocalSetupJournal.Committing) await LocalSetupCommit.PublishAsync(lease, entry, details);

        if (!service.IsInstalled(unit)) {
            await ReinstallAsync(lease.Root, unit);
            await LocalServerReadiness.WaitAsync(local, unit, LocalServerReadiness.StartBudget, time, CancellationToken.None);
        }

        await LocalSetupCommit.ConfirmAsync(lease, details, service, home, local, time);
    }

    /// <summary>Only into an empty slot or over this root's own job: the unit is per user.</summary>
    async Task ReinstallAsync(LocalRoot root, LocalServerUnit unit) {
        using var _ = await LocalUnitLock.AcquireAsync(home, UnitLockWait, TimeProvider.System, CancellationToken.None);

        var owner = service.Owner(unit);
        var empty = owner is { Loaded: OwnerReading.Absent, OnDisk: OwnerReading.Absent };

        if (!empty && LocalRootIdentity.Decide(root, owner) != OwnerDecision.Ours)
            throw Failed($"The interrupted setup cannot be finished: its {service.Describe} {unit.Label} ({service.UnitFile(unit)}) is missing, and the job loaded under that label is not provably this installation's. The setup journal {root.JournalPath} is kept.");

        service.Install(unit);
    }

    static LocalInstallFailure Failed(string message) => new(LocalInstallFailureCode.RecoveryFailed, message);
}
