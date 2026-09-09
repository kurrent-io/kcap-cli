using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.Cli.Core;

namespace Capacitor.App.Services;

/// Whether the daemon has a restart-after-update queued, read from the marker file it writes
/// and its successor deletes at startup — the same source `kcap daemon status` reads, since
/// nothing about a queued restart travels over the status socket. Re-read on every attach
/// transition and on a poll matching the daemon's own binary poll.
public sealed class DaemonRestartPendingWatcher : IDisposable {
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    readonly DaemonStore _store;
    readonly string _daemonName;
    readonly IObservable<AttachStatus> _status;
    readonly TimeProvider _time;
    // Owned, so disposal ends the poll even when the caller's lifetime token is never cancelled
    // (startup-failure cleanup disposes the graph's pieces without cancelling it).
    readonly CancellationTokenSource _lifetime;
    readonly BehaviorSubject<bool> _pending = new(false);
    readonly Lock _lock = new();
    IDisposable? _subscription;
    bool _disposed;

    public DaemonRestartPendingWatcher(
            DaemonStore store, string daemonName, IObservable<AttachStatus> status, TimeProvider time,
            CancellationToken lifetime) {
        _store      = store;
        _daemonName = daemonName;
        _status     = status;
        _time       = time;
        _lifetime   = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
    }

    /// True while the marker exists. Replays the current value to a new subscriber.
    public IObservable<bool> Pending => _pending.DistinctUntilChanged();

    public void Start() {
        Refresh();
        _subscription = _status.Subscribe(_ => Refresh());
        // The token value stays readable after Dispose has disposed its source; the source's
        // Token property would not.
        _ = PollAsync(_lifetime.Token);
    }

    // The attach stream and the poll call this from their own threads. The read sits inside the
    // lock with the publish: read outside it, two callers could publish their observations in
    // the reverse order of their reads and briefly resurrect a marker that was already gone.
    void Refresh() {
        lock (_lock) {
            if (_disposed) return;
            _pending.OnNext(DaemonRestartMarker.TryRead(_store, _daemonName) is not null);
        }
    }

    async Task PollAsync(CancellationToken ct) {
        try {
            while (!ct.IsCancellationRequested) {
                await Task.Delay(PollInterval, _time, ct).ConfigureAwait(false);
                Refresh();
            }
        } catch (OperationCanceledException) {
            // shutdown or disposal
        }
    }

    public void Dispose() {
        lock (_lock) {
            if (_disposed) return;
            _disposed = true;
        }
        _subscription?.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
