using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>
/// Undoes a setup that had not reached <c>committing</c>: unloads its unit, then deletes what it created and clears
/// the journal. Runs on its own token, since the install's is usually the one that was cancelled.
///
/// <para>Nothing is deleted until the unit is proven unable to start this root's server. A failure before that keeps
/// the journal exactly as it was, so the next lifecycle command makes another verified unload first.</para>
/// </summary>
static class LocalSetupRollback {
    static readonly string[] Created = ["bin", "data", "run"];

    /// <summary>Throws <see cref="LocalInstallFailure"/> with <see cref="LocalInstallFailureCode.RecoveryFailed"/>
    /// when it stopped short. <see cref="LocalInstallEvent.RollingBack"/> is reported once nothing can start this
    /// root's server, and <see cref="LocalInstallEvent.RolledBack"/> only when it finished.</summary>
    public static async Task RunAsync(LifecycleLease lease, LifecycleJournalEntry entry, ILocalServerService service, UserHome home,
        IProgress<LocalInstallEvent> progress, string reason) {
        var root = lease.Root;

        if (!LocalSetupJournal.TryRead(entry, out var details, out var error))
            throw Stopped($"The setup journal {root.JournalPath} cannot be acted on ({error}). Nothing was removed.");

        var unit = LocalServerUnit.ForInstallation(root, details.Port);

        // At pending no Install was attempted, and the empty-slot check proved no unit existed.
        var unitPhase = entry.Phase == LocalSetupJournal.Unit;

        using (unitPhase ? await LocalUnitLock.AcquireAsync(home, LocalUnitLock.Wait, TimeProvider.System, CancellationToken.None) : null) {
            if (unitPhase) {
                FaultPoints.Hit("rollback:unit");
                await UnloadAsync(root, unit, service);
            }

            if (LocalStackProbe.FindLive(root) is { } live)
                throw Stopped($"Setup could not be undone: {live} under {root.Directory}. Nothing was removed.");

            progress.TryReport(new LocalInstallEvent.RollingBack(reason));
            FaultPoints.Hit("rollback:delete");

            foreach (var name in Created) Delete(Path.Combine(root.Directory, name));

            DurableDirectory.Sync(root.Directory);

            // An earlier attempt may have removed the unit file and died before syncing its directory.
            if (unitPhase && Path.GetDirectoryName(service.UnitFile(unit)) is { } unitDir && Directory.Exists(unitDir))
                DurableDirectory.Sync(unitDir);

            lease.Complete();
        }

        progress.TryReport(new LocalInstallEvent.RolledBack());
    }

    static async Task UnloadAsync(LocalRoot root, LocalServerUnit unit, ILocalServerService service) {
        switch (LocalRootIdentity.Decide(root, service.Owner(unit))) {
            case OwnerDecision.Ours:
                try {
                    await service.UninstallAsync(unit, CancellationToken.None);
                } catch (Exception ex) {
                    throw Stopped($"Setup could not be undone: the {service.Describe} {unit.Label} could not be unloaded ({ex.Message}). Nothing was removed.");
                }

                break;

            case OwnerDecision.NotOurs:
                // Nothing present can start this root, and a foreign job is never stopped to prove it.
                break;

            default:
                throw Stopped($"Setup could not be undone: it cannot tell which installation the {service.Describe} {unit.Label} ({service.UnitFile(unit)}) belongs to. Nothing was removed.");
        }
    }

    // Directory.Delete removes a symlink, at the top or anywhere in the tree, without following it.
    static void Delete(string path) {
        if (new DirectoryInfo(path).LinkTarget is not null || File.Exists(path)) File.Delete(path);
        else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        else return;

        FaultPoints.Hit($"unlink:{path}");
    }

    static LocalInstallFailure Stopped(string message) => new(LocalInstallFailureCode.RecoveryFailed, message);
}
