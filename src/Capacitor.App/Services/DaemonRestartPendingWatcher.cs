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
    readonly CancellationToken _lifetime;
    readonly BehaviorSubject<bool> _pending = new(false);
    readonly Lock _lock = new();
    IDisposable? _subscription;

    public DaemonRestartPendingWatcher(
            DaemonStore store, string daemonName, IObservable<AttachStatus> status, TimeProvider time,
            CancellationToken lifetime) {
        _store      = store;
        _daemonName = daemonName;
        _status     = status;
        _time       = time;
        _lifetime   = lifetime;
    }

    /// True while the marker exists. Replays the current value to a new subscriber.
    public IObservable<bool> Pending => _pending.DistinctUntilChanged();

    public void Start() {
        Refresh();
        _subscription = _status.Subscribe(_ => Refresh());
        _ = PollAsync();
    }

    // Both the attach stream and the poll call this from their own threads; the subject's
    // OnNext must never overlap.
    void Refresh() {
        var pending = DaemonRestartMarker.TryRead(_store, _daemonName) is not null;
        lock (_lock) _pending.OnNext(pending);
    }

    async Task PollAsync() {
        try {
            while (!_lifetime.IsCancellationRequested) {
                await Task.Delay(PollInterval, _time, _lifetime).ConfigureAwait(false);
                Refresh();
            }
        } catch (OperationCanceledException) {
            // shutdown
        }
    }

    public void Dispose() => _subscription?.Dispose();
}
