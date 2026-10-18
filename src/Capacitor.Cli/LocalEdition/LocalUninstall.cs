using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>
/// Removes the installation under the lifecycle lease, in order: refuse while a daemon runs agents unless forced;
/// stop and verify every daemon and watcher; stop this root's unit and verify nothing of its stack is alive; only then
/// remove the unit, <c>bin/</c> and <c>run/</c>. Without a purge the registry and <c>data/</c> stay, so a reinstall
/// returns as the same installation on the same port. A purge then retires it (see <see cref="LocalPurge"/>).
/// </summary>
sealed class LocalUninstall(ILocalServerService service, UserHome home, IOutgoingRecorders recorders) {
    /// <summary>Throws <see cref="LocalLifecycleException"/> naming what stopped it; nothing is deleted before the
    /// stops are verified.</summary>
    public async Task<LocalUninstallOutcome> RunAsync(LifecycleLease lease, LocalStackState state, bool purge, bool force, string configDir,
        CancellationToken ct) {
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

        await LocalStackStop.RunAsync(lease.Root, LocalServerUnit.ForInstallation(lease.Root, installation.Port), service, home, ct);

        LocalStackStop.Delete(lease.Root, "bin", "run");
        FaultPoints.Hit("uninstall:after-delete");

        if (!purge) return LocalUninstallOutcome.Uninstalled;

        var entry = LocalPurge.Begin(lease, installation, configDir);
        FaultPoints.Hit("purge:begun");

        await LocalPurge.FinishAsync(lease, entry, service, home, ct);

        return LocalUninstallOutcome.Purged;
    }
}
