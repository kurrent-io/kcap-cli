using Capacitor.Cli.Core;

namespace Capacitor.Cli.Services;

/// <summary>
/// Judges a transaction marker no live transaction holds against what the service shows now.
/// </summary>
internal static class StaleTxnMarker {
    /// <summary>
    /// True when the interrupted transaction provably took effect: the job is running, the validated
    /// daemon is that job, and the unit on disk is the one the marker recorded writing. A unit that
    /// differs from the recorded fingerprint was touched by someone else and stays surfaced.
    /// </summary>
    public static bool TookEffect(
            TxnMarker marker, ServiceQuery query, int? daemonPid, LaunchdUnit.PlistRead plist, string? onDisk) {
        if (query.State != ServiceState.Running || query.JobPid is null || daemonPid != query.JobPid) return false;
        if (marker.PlistFingerprint is null) return true;

        return plist == LaunchdUnit.PlistRead.Ok
            && onDisk is not null
            && ServiceTxnMarker.Fingerprint(onDisk) == marker.PlistFingerprint;
    }

    /// <summary>
    /// Deletes a leftover marker whose transaction took effect. Holds the transaction lock for the
    /// read and the delete, and does nothing when another operation holds it.
    /// </summary>
    public static async Task SettleAsync(DaemonStore store, IServiceManager manager, string serviceId, TimeProvider time) {
        if (manager is not LaunchdServiceManager launchd || !ServiceTxnMarker.Exists(store, serviceId)) return;

        using var txn = await ServiceTxnLock.TryAcquireAsync(store, serviceId, TimeSpan.Zero, time).ConfigureAwait(false);
        if (txn is null || ServiceTxnMarker.Read(store, serviceId) is not { } marker) return;

        var query     = manager.Query(serviceId);
        var daemonPid = DaemonPidProbe.ValidatedPid(store, serviceId);
        var plist     = LaunchdUnit.TryReadPlist(launchd.UnitPath(serviceId), out var onDisk);

        if (TookEffect(marker, query, daemonPid, plist, onDisk)) ServiceTxnMarker.Delete(store, serviceId);
    }
}
