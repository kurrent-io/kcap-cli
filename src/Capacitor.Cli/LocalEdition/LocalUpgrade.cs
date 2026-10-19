using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>
/// <c>kcap local upgrade</c>, the only code that changes the stack's version. The stack only moves forward: a caller
/// older than it, or at its version, changes nothing.
///
/// <para>Under the lifecycle lease: staged beside the old version, which keeps running; stopped, with the unit
/// unloaded and nothing of the stack alive; switched, the registry naming the new version before <c>bin/current</c>
/// does, so no older kcap can select a version below data the new server may have migrated; started under a
/// rewritten unit; committed by clearing the journal. A failure before the switch starts the old version again if it
/// was running. After it nothing is restored: the new version stays selected and stopped.</para>
/// </summary>
sealed class LocalUpgrade(LocalStack stack, IProgress<LocalInstallEvent> progress) {
    const string StagingName = "upgrade";

    LocalStackControl Control => new(stack, stack.FindHolder);

    /// <summary>Throws <see cref="LocalUpgradeFailure"/>, <see cref="LocalInstallFailure"/> from a recovery, or
    /// <see cref="LocalLifecycleException"/> when the lease is held or the journal cannot be acted on.</summary>
    /// <param name="stager">Built only once there is something to stage.</param>
    public Task<LocalInstallEvent> RunAsync(Func<LocalBundleStager> stager, string version, CancellationToken ct) =>
        stack.RunAsync(progress, (lease, state, token) => UnderLeaseAsync(lease, state, stager, version, token), ct);

    async Task<LocalInstallEvent> UnderLeaseAsync(LifecycleLease lease, LocalStackState state, Func<LocalBundleStager> stager, string version,
        CancellationToken ct) {
        var installed = state switch {
            LocalStackState.Installed i => i,
            LocalStackState.NotInstalled => null,
            LocalStackState.Unreadable(var path, var reason) =>
                throw Failed(LocalInstallFailureCode.RegistryUnreadable, $"{path} cannot be read ({reason}). Nothing was changed."),
            _ => throw new InvalidOperationException($"Unexpected state {state} under the lifecycle lease."),
        };

        if (installed is null) return Done(new LocalInstallEvent.NothingToDo(UpgradeSkip.NotInstalled, version, null));

        var (installation, unit) = (installed.Installation, installed.Unit);
        var from = installation.Version;

        switch (PrereleaseSemver.Compare(version, from)) {
            case 0:  return Done(new LocalInstallEvent.NothingToDo(UpgradeSkip.Current, version, from));
            case < 0: return Done(new LocalInstallEvent.NothingToDo(UpgradeSkip.StackNewer, version, from));
        }

        if (installed.UnitAbsent || !File.Exists(unit.ServerBinary))
            throw Failed(LocalInstallFailureCode.StartFailed,
                $"The local server is uninstalled. Run kcap setup --local to reinstall Capacitor local {from}, then kcap local upgrade.");

        if (installed.Owner != OwnerDecision.Ours)
            throw Failed(LocalInstallFailureCode.NotOurServer,
                $"The {stack.Service.Describe} {unit.Label} ({stack.Service.UnitFile(unit)}) is not provably this installation's. Nothing was changed.");

        if (LocalBin.Current(stack.Root) == version)
            throw Failed(LocalInstallFailureCode.LeftoverState,
                $"bin/current already names {version} while the registry names {from}. Nothing was changed.");

        var details = new UpgradeDetails(from, version, installation.Port, stack.Service.Activity(unit) is UnitActivity.Running or UnitActivity.Restarting);

        progress.Report(new LocalInstallEvent.UpgradePlanned(from, version));

        var entry = lease.Begin(LocalUpgradeJournal.Operation, LocalUpgradeJournal.Staging, ServerStart.Allow, installation.Id,
            LocalUpgradeJournal.Build(details));

        try {
            await stager().StageAsync(version, StagingName, progress, ct);
        } catch (Exception ex) {
            try {
                LocalBin.Discard(stack.Root, StagingName, version);
                lease.Complete();
            } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) {
                // The journal stays at staging, and the next lifecycle command removes what is left.
            }

            throw Failed(Classify(ex, ct), $"{Describe(ex, ct)} Capacitor local {from} was left as it was.");
        }

        entry = lease.Advance(entry, LocalUpgradeJournal.Stopping, ServerStart.Deny);
        FaultPoints.Hit("upgrade:stopping");
        progress.Report(new LocalInstallEvent.Stopping());

        try {
            await Control.StopAsync(installed, _ => { }, CancellationToken.None);

            if (LocalStackProbe.FindLive(stack.Root) is { } live)
                throw new LocalLifecycleException($"The local server's service stopped, but {live}.");
        } catch (Exception ex) {
            var (_, restart) = await RestartOldAsync(lease, entry, details);

            throw Failed(LocalInstallFailureCode.StartFailed, $"The local server could not be stopped ({ex.Message}). {restart}");
        }

        progress.Report(new LocalInstallEvent.Stopped());

        entry = lease.Advance(entry, LocalUpgradeJournal.Switching, ServerStart.Deny);
        Switch(lease, entry, details);

        return await StartNewAsync(lease, entry, details);
    }

    /// <summary>Finishes an upgrade the previous lifecycle command left in the journal.</summary>
    public async Task ResumeAsync(LifecycleLease lease, LifecycleJournalEntry entry, UpgradeDetails details) {
        switch (entry.Phase) {
            case LocalUpgradeJournal.Staging:
                LocalBin.Discard(stack.Root, StagingName, details.To);
                lease.Complete();
                return;

            case LocalUpgradeJournal.Stopping or LocalUpgradeJournal.Restarting:
                var (started, restart) = await RestartOldAsync(lease, entry, details);
                if (!started) throw Failed(LocalInstallFailureCode.RecoveryFailed, $"An interrupted upgrade to {details.To} was undone. {restart}");
                return;

            case LocalUpgradeJournal.Switching:
                if (!LocalBin.Has(stack.Root, details.To))
                    throw new LocalLifecycleException(
                        $"An interrupted upgrade to {details.To} cannot be finished: {Path.Combine(LocalBin.Directory(stack.Root), details.To)} is missing. The upgrade journal {lease.Root.JournalPath} is kept.");

                Switch(lease, entry, details);
                await StartNewAsync(lease, entry, details);
                return;

            case LocalUpgradeJournal.Starting:
                await StartNewAsync(lease, entry, details);
                return;

            default:
                throw new InvalidOperationException($"Unexpected upgrade phase {entry.Phase}.");
        }
    }

    /// <summary>A throw here leaves the journal at <c>switching</c>: the registry may already name the new version, so
    /// only rolling forward is safe, and the next lifecycle command does it.</summary>
    void Switch(LifecycleLease lease, LifecycleJournalEntry entry, UpgradeDetails details) {
        try {
            PublishVersion(lease, entry.InstallationId, details.To);
            FaultPoints.Hit("upgrade:published");

            LocalBin.Switch(stack.Root, details.To);
        } catch (Exception ex) {
            throw new LocalUpgradeFailure(LocalInstallFailureCode.CommitFailed,
                $"The switch to Capacitor local {details.To} could not be confirmed ({ex.Message}). Nothing was restored; the next kcap local upgrade, start or setup --local finishes it.",
                switched: true);
        }

        progress.Report(new LocalInstallEvent.Switched(details.To));
    }

    async Task<LocalInstallEvent> StartNewAsync(LifecycleLease lease, LifecycleJournalEntry entry, UpgradeDetails details) {
        lease.Advance(entry, LocalUpgradeJournal.Starting, ServerStart.Allow);
        FaultPoints.Hit("upgrade:starting");

        var installed = Installed();
        progress.Report(new LocalInstallEvent.Starting(installed.Unit.Label));

        try {
            await Control.StartAsync(installed, _ => { }, CancellationToken.None, rewriteUnit: true);
        } catch (Exception ex) {
            var why = ex.Message;

            try {
                await Control.StopAsync(installed, _ => { }, CancellationToken.None);
            } catch (Exception stop) {
                why += $" It could not be stopped either ({stop.Message}).";
            }

            lease.Complete();

            throw new LocalUpgradeFailure(LocalInstallFailureCode.StartFailed,
                $"Capacitor local {details.To} did not start: {why} It stays selected and stopped, and {details.From} is kept in "
              + $"{Path.Combine(LocalBin.Directory(stack.Root), details.From)}. Nothing is rolled back; kcap local logs shows why, and kcap local start tries again.",
                switched: true);
        }

        progress.Report(new LocalInstallEvent.Ready(installed.Installation.ServerUrl));

        lease.Complete();

        try {
            LocalBin.Discard(stack.Root, StagingName, details.From);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // The upgrade is committed; an old bundle left behind only costs disk.
        }

        return Done(new LocalInstallEvent.Upgraded(installed.Installation.ServerUrl, details.From, details.To));
    }

    /// <summary>Whether the old version is back as it was, and a sentence saying so; never throws.</summary>
    async Task<(bool Restored, string Outcome)> RestartOldAsync(LifecycleLease lease, LifecycleJournalEntry entry, UpgradeDetails details) {
        lease.Advance(entry, LocalUpgradeJournal.Restarting, ServerStart.Allow);

        (bool, string) outcome;

        try {
            LocalBin.Discard(stack.Root, StagingName, details.To);

            if (details.WasRunning) {
                progress.Report(new LocalInstallEvent.Restarting(details.From));
                await Control.StartAsync(Installed(), _ => { }, CancellationToken.None);
                outcome = (true, $"Capacitor local {details.From} was started again.");
            } else {
                outcome = (true, $"Capacitor local {details.From} stays selected and stopped, as it was.");
            }
        } catch (Exception ex) {
            outcome = (false, $"Capacitor local {details.From} stays selected but did not start again ({ex.Message}); kcap local start tries again.");
        }

        lease.Complete();

        return outcome;
    }

    LocalStackState.Installed Installed() =>
        stack.ReadUnderLease() as LocalStackState.Installed
     ?? throw new LocalLifecycleException("The installation registry no longer names an active installation.");

    static void PublishVersion(LifecycleLease lease, string? installationId, string version) {
        if (lease.ReadRegistry() is not StateRead<InstallRegistry>.Present { Value: { Active: { } active } registry } || active.Id != installationId)
            throw new LocalLifecycleException("the installation registry no longer names the installation being upgraded");

        if (active.Version != version) lease.PublishRegistry(registry with { Active = active with { Version = version } });
    }

    LocalInstallEvent Done(LocalInstallEvent result) {
        progress.TryReport(result);
        return result;
    }

    static LocalUpgradeFailure Failed(LocalInstallFailureCode code, string message) => new(code, message, switched: false);

    static LocalInstallFailureCode Classify(Exception ex, CancellationToken ct) =>
        ex switch {
            _ when ct.IsCancellationRequested => LocalInstallFailureCode.Cancelled,
            LocalInstallFailure failure       => failure.Code,
            LocalBundleException bundle       => bundle.Code,
            _                                 => LocalInstallFailureCode.DownloadFailed,
        };

    static string Describe(Exception ex, CancellationToken ct) => ct.IsCancellationRequested ? "The upgrade was cancelled." : ex.Message;
}
