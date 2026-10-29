using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.Cli.Services;

static class ServiceRemoval {
    /// <summary>Asks a running daemon whose unit is being removed for good to end its agents and exit: the
    /// bootout alone reaches it as SIGTERM, which keeps them for a successor that never starts. One that
    /// does not answer is left to the bootout. False when the daemon was running and did not exit: its
    /// ledger still names agents a later start would resume.</summary>
    internal static async Task<bool> EndAgentsAsync(DaemonStore store, string serviceId, TimeProvider time) {
        if (DaemonPidProbe.ValidatedPid(store, serviceId) is null) return true;

        return await DaemonShutdownClient.StopAndWaitAsync(
                store, serviceId, new ShutdownRequestDto("end", "final"), DaemonCommands.StopExitWait, time, CancellationToken.None)
            || DaemonPidProbe.ValidatedPid(store, serviceId) is null;
    }
}
