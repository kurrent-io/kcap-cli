namespace Capacitor.Cli.Daemon.Services;

/// Polls a vendor's session tree for a freshly spawned agent's transcript until the file is
/// known or the agent goes away. Runs until the PATH is known — a session id learned some
/// other way is not a reason to stop, since the path is what the desktop app reads. Returns
/// false on cancellation of `ct`, true once `onFound` has run; a fault from `locate` or
/// `onFound` propagates to the caller.
internal sealed class TranscriptDiscovery(TimeProvider time, TimeSpan interval) {
    public async Task<bool> RunAsync(
            Func<ISet<string>, (string SessionId, string Path)?> locate,
            Func<(string SessionId, string Path), Task> onFound,
            CancellationToken ct) {
        var ruledOut = new HashSet<string>();
        (string SessionId, string Path)? winner = null;

        try {
            while (winner is null) {
                if (ct.IsCancellationRequested) return false;
                winner = locate(ruledOut);
                if (winner is null) await Task.Delay(interval, time, ct).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) {
            // The agent exited or the daemon is shutting down — nothing left to find.
            return false;
        }

        await onFound(winner.Value).ConfigureAwait(false);
        return true;
    }
}
