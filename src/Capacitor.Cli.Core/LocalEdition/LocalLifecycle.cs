namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>
/// Runs a lifecycle command: takes the lock, finishes or undoes whatever an interrupted command left
/// in the journal, then runs the command. Every lifecycle command goes through here, so a crash at
/// any phase is recovered by whichever command comes next.
/// </summary>
public sealed class LocalLifecycle(LocalRoot root, IEnumerable<ILifecycleRecovery> recoveries, TimeProvider time) {
    readonly Dictionary<string, ILifecycleRecovery> _recoveries = recoveries.ToDictionary(r => r.Operation, StringComparer.Ordinal);

    public LocalRoot Root { get; } = root;

    /// <summary>Runs the optional read-only consent check under the lease before recovery or mutation.</summary>
    public async Task<T> RunAsync<T>(TimeSpan wait, Func<LifecycleLease, CancellationToken, Task<T>> command, CancellationToken ct,
        Action<LifecycleLease>? beforeRecovery = null) {
        using var lease = await LifecycleLease.AcquireAsync(Root, wait, time, ct);

        beforeRecovery?.Invoke(lease);
        await RecoverAsync(lease, ct);

        return await command(lease, ct);
    }

    async Task RecoverAsync(LifecycleLease lease, CancellationToken ct) {
        switch (lease.ReadJournal()) {
            case StateRead<LifecycleJournalEntry>.Absent:
                return;

            case StateRead<LifecycleJournalEntry>.Unreadable(var path, var reason):
                throw new LocalLifecycleException($"The lifecycle journal {path} cannot be read ({reason}). Nothing was changed.");

            case StateRead<LifecycleJournalEntry>.Present(var interrupted):
                // An operation this build does not know is left exactly as it was: undoing it blind could
                // delete what a newer kcap meant to keep.
                if (!_recoveries.TryGetValue(interrupted.Operation, out var recovery))
                    throw new LocalLifecycleException(
                        $"An interrupted '{interrupted.Operation}' of the local installation needs a newer kcap to finish it. Update kcap, then try again.");

                await recovery.RecoverAsync(lease, interrupted, ct);

                if (lease.ReadJournal() is not StateRead<LifecycleJournalEntry>.Absent)
                    throw new InvalidOperationException($"Recovery of '{interrupted.Operation}' did not complete the journal.");

                return;
        }
    }
}
