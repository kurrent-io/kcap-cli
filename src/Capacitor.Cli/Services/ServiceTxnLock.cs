using Capacitor.Cli.Core;

namespace Capacitor.Cli.Services;

/// <summary>
/// Per-label cross-process lock for serializing mutating <c>kcap daemon service</c> verbs.
/// Lock file lives in the daemons directory, distinct from <see cref="DaemonStore.LockPath"/>,
/// and is never unlinked.
/// </summary>
sealed class ServiceTxnLock : IDisposable {
    readonly FileStream _stream;

    ServiceTxnLock(FileStream stream) => _stream = stream;

    /// <summary>
    /// Non-blocking probe: true iff some process currently holds the lock.
    /// </summary>
    public static bool IsHeld(DaemonStore store, string daemonName) {
        var path = store.ServiceLockPath(daemonName);

        if (!File.Exists(path)) return false;

        try {
            using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return false;
        } catch (IOException) {
            return true;
        }
    }

    static readonly TimeSpan PollGap = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Waits up to <paramref name="wait"/>; null on contention timeout. Lock file is created but NEVER deleted.
    /// </summary>
    /// <remarks>The gap between attempts is drawn from <paramref name="time"/>, like the deadline it is
    /// measured against: a sleep on the wall clock under a caller's slower one would spin until the
    /// deadline it can never reach.</remarks>
    public static async Task<ServiceTxnLock?> TryAcquireAsync(
            DaemonStore store, string daemonName, TimeSpan wait, TimeProvider time) {
        store.EnsureDirectory();
        var path     = store.ServiceLockPath(daemonName);
        var deadline = time.GetUtcNow().Add(wait);

        while (true) {
            try {
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return new ServiceTxnLock(stream);
            } catch (IOException) {
                if (time.GetUtcNow() >= deadline) return null;

                await Task.Delay(PollGap, time).ConfigureAwait(false);
            }
        }
    }

    public void Dispose() => _stream.Dispose();
}
