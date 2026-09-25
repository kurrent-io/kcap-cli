using Capacitor.Cli.Core.Eval.Evidence;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>One phase's hold on a cached evidence run: the run is not disposed while a lease is open, and
/// <see cref="Cancelled"/> fires when the run leaves the cache, so the phase can stop.</summary>
internal sealed class EvidenceRunLease(EvidenceRunSetup setup, CancellationToken cancelled, Action end) : IDisposable {
    int _ended;

    public EvidenceRunSetup  Setup     => setup;
    public CancellationToken Cancelled => cancelled;

    public void Dispose() {
        if (Interlocked.Exchange(ref _ended, 1) == 0) end();
    }
}
