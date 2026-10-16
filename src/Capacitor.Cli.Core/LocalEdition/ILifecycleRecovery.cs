namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>Finishes or undoes one kind of interrupted operation. Runs under the lease, before any
/// other lifecycle command, and must end with <see cref="LifecycleLease.Complete"/>.</summary>
public interface ILifecycleRecovery {
    /// <summary>The <see cref="LifecycleJournalEntry.Operation"/> this recovers.</summary>
    string Operation { get; }

    Task RecoverAsync(LifecycleLease lease, LifecycleJournalEntry interrupted, CancellationToken ct);
}
