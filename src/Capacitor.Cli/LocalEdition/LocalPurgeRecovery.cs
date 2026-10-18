using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>
/// Finishes a purge the previous lifecycle command left in the journal. A purge is never undone: from either phase it
/// runs on to the end, once no daemon or watcher records any more. The journal denies the server a start but not them,
/// so one may have started since the crash.
///
/// <para>With <paramref name="mayStop"/> they are stopped, refusing while a daemon runs agents unless
/// <paramref name="force"/>; without it, one still alive leaves the journal for <c>kcap local uninstall --purge</c>.
/// The watchers this process can see are its own config root's, so a purge begun under another one is left for a
/// command run under that root.</para>
/// </summary>
sealed class LocalPurgeRecovery(
    ILocalServerService service, UserHome home, IOutgoingRecorders recorders, ConfigRoot config, bool mayStop, bool force,
    IProgress<LocalInstallEvent> progress)
    : ILifecycleRecovery {
    public string Operation => LocalPurgeJournal.Operation;

    public async Task RecoverAsync(LifecycleLease lease, LifecycleJournalEntry interrupted, CancellationToken ct) {
        if (!LocalPurgeJournal.TryRead(interrupted, out var details, out var error))
            throw Kept($"The purge journal {lease.Root.JournalPath} cannot be acted on ({error}). Nothing was changed; fix or remove it, then run kcap local uninstall --purge again.");

        if (!PathComparison.Equal(Path.GetFullPath(details.ConfigDir), Path.GetFullPath(config.Directory)))
            throw Kept($"An interrupted purge began under the config directory {details.ConfigDir}, whose watchers this kcap cannot see. Nothing was changed; run kcap local uninstall --purge with KCAP_CONFIG_DIR={details.ConfigDir}.");

        progress.Report(new LocalInstallEvent.Recovering(Operation, interrupted.Phase));

        if (mayStop) {
            if (!force && await recorders.BusyAsync() is { Count: > 0 } busy)
                throw Kept($"An interrupted purge cannot finish while a daemon is running agents ({string.Join("; ", busy)}). Stop them, or pass --force. The purge journal is kept.");

            if (!await recorders.StopAsync())
                throw Kept("An interrupted purge cannot finish: a daemon or watcher could not be stopped. The purge journal is kept.");
        }

        if (recorders.Live() is { Count: > 0 } live)
            throw Kept(
                $"An interrupted purge cannot finish while {string.Join(", ", live)} still records. The purge journal is kept; run kcap local uninstall --purge to stop them and finish it.");

        await LocalPurge.FinishAsync(lease, interrupted, service, home, ct);

        progress.TryReport(new LocalInstallEvent.Recovered(Operation, interrupted.Phase));
    }

    static LocalLifecycleException Kept(string message) => new(message);
}
