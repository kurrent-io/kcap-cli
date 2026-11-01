using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>
/// Removes the installation under the lifecycle lease, in order: refuse while a daemon runs agents unless forced;
/// stop and verify every daemon and watcher; stop this root's unit and verify nothing of its stack is alive; only then
/// remove the unit, <c>bin/</c> and <c>run/</c>. Without a purge the registry and <c>data/</c> stay, so a reinstall
/// returns as the same installation on the same port. A purge removes nothing until it has retired the installation
/// (see <see cref="LocalPurge"/>).
/// </summary>
sealed class LocalUninstall(ILocalServerService service, UserHome home, IOutgoingRecorders recorders) {
    /// <summary>Set, the uninstall gets this long, counted from this object's creation and so including every wait for a
    /// lock, before the server's unit is removed regardless; what is still running keeps <c>bin/</c> and <c>run/</c>.
    /// For an app uninstaller with a deadline; it cannot purge.</summary>
    public TimeSpan? QuickGrace { get; init; }

    readonly long _created = TimeProvider.System.GetTimestamp();

    /// <summary>A server is asked to stop with at least this long, however much of the grace the locks took.</summary>
    static readonly TimeSpan MinimumStopGrace = TimeSpan.FromSeconds(1);

    /// <summary>Throws <see cref="LocalLifecycleException"/> naming what stopped it; nothing is deleted before the
    /// stops are verified.</summary>
    public async Task<LocalUninstallOutcome> RunAsync(LifecycleLease lease, LocalStackState state, bool purge, bool force, string configDir,
        CancellationToken ct) {
        if (QuickGrace is not null && purge) throw new LocalLifecycleException("A quick uninstall cannot purge. Nothing was changed.");

        var installation = state switch {
            LocalStackState.Installed installed           => installed.Installation,
            LocalStackState.NotInstalled                  => null,
            LocalStackState.Unreadable(var path, var why) => throw new LocalLifecycleException($"{path} cannot be read ({why}). Nothing was changed."),
            _                                             => throw new LocalLifecycleException($"The local installation is in an unexpected state ({state}). Nothing was changed."),
        };

        if (installation is null) return LocalUninstallOutcome.NotInstalled;

        if (!force && await recorders.BusyAsync() is { Count: > 0 } busy)
            throw new LocalLifecycleException(
                $"A daemon is running agents ({string.Join("; ", busy)}). Stop them, or pass --force to end them with the daemon. Nothing was changed.");

        if (!await recorders.StopAsync())
            throw new LocalLifecycleException("A daemon or watcher could not be stopped, so the local installation was left as it is. Run kcap local uninstall again.");

        FaultPoints.Hit("uninstall:recorders-stopped");

        var unit = LocalServerUnit.ForInstallation(lease.Root, installation.Port);

        if (QuickGrace is { } grace) {
            TimeSpan Left() {
                var left = grace - TimeProvider.System.GetElapsedTime(_created);
                return left > MinimumStopGrace ? left : MinimumStopGrace;
            }

            if (!await LocalStackStop.RunWithinAsync(lease.Root, unit, service, home, Left, ct)) return LocalUninstallOutcome.Unloaded;

            try {
                LocalStackStop.Delete(lease.Root, "bin", "run");
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // The server let go of the root but its process has not exited yet, and its files are still open.
                return LocalUninstallOutcome.Unloaded;
            }

            return LocalUninstallOutcome.Uninstalled;
        }

        if (!purge) {
            await LocalStackStop.RunAsync(lease.Root, unit, service, home, removeUnit: true, ct);

            LocalStackStop.Delete(lease.Root, "bin", "run");
            FaultPoints.Hit("uninstall:after-delete");

            return LocalUninstallOutcome.Uninstalled;
        }

        // Stopped, not removed: a purge deletes nothing until its retirement is durable.
        await LocalStackStop.RunAsync(lease.Root, unit, service, home, removeUnit: false, ct);

        var entry = LocalPurge.Begin(lease, installation, configDir, recorders.WatcherDirectory);
        FaultPoints.Hit("purge:begun");

        await LocalPurge.FinishAsync(lease, entry, service, home, ct);

        return LocalUninstallOutcome.Purged;
    }
}
