using Capacitor.Cli.Core.Resume;

namespace Capacitor.Cli.Local;

/// <summary>How an attach rides out a daemon restart: re-attach by <paramref name="AgentId"/> for up to
/// <paramref name="Window"/>, for as long as <paramref name="Ledger"/> holds an entry that is not
/// interrupted.</summary>
internal sealed record AttachReconnect(string AgentId, TimeSpan Window, Func<ResumeLedgerEntry?> Ledger) {
    public TimeSpan RetryGap { get; init; } = TimeSpan.FromSeconds(1);
}
