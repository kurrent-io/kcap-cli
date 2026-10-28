using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace Capacitor.App.Services;

/// <summary>
/// Tells an outage the daemon chose apart from one it suffered. A daemon that queued a restart after
/// an update leaves its restart-pending marker on disk until its successor starts, and an app that has
/// just been updated expects its daemon to follow; while either holds, an unreachable daemon is
/// reported with <see cref="Reason"/> instead of <c>daemon_unreachable</c>, and the attach is retried
/// every <see cref="RetryInterval"/> rather than on the connect backoff. Past <see cref="Grace"/> the
/// outage reads as an ordinary one again, so a restart that never comes back still gets its Start button.
/// </summary>
public sealed class ExpectedDaemonRestart : IDisposable {
    public const string Reason = "daemon_restarting";
    const string UnreachableReason = "daemon_unreachable";

    internal static readonly TimeSpan Grace = TimeSpan.FromSeconds(90);
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(1);

    readonly Func<bool> _appJustUpdated;
    readonly Action<bool, TimeSpan>? _outageEnded;
    readonly Func<Task> _reattach;
    readonly TimeProvider _time;
    readonly ReplaySubject<AttachStatus> _status = new(1);
    readonly Lock _lock = new();
    readonly IDisposable _subscription;
    readonly ITimer _timer;

    AttachStatus? _raw;
    bool _marker;
    // From the first unreachable report after the last Connected: a retry's Connecting does not end the outage.
    DateTimeOffset? _outageSince;
    string? _outageReason;
    bool _outageExpected;
    bool _disposed;

    /// <param name="outageEnded">Told, on reconnecting, whether the outage was expected and how long it lasted.</param>
    public ExpectedDaemonRestart(
            IObservable<AttachStatus> status, IObservable<bool> restartPending, Func<bool> appJustUpdated,
            Func<Task> reattach, TimeProvider time, Action<bool, TimeSpan>? outageEnded = null) {
        _appJustUpdated = appJustUpdated;
        _outageEnded = outageEnded;
        _reattach = reattach;
        _time = time;
        _timer = time.CreateTimer(_ => Tick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _subscription = new System.Reactive.Disposables.CompositeDisposable(
            restartPending.Subscribe(pending => Update(() => _marker = pending)),
            status.Subscribe(next => Update(() => {
                if (next.State == AttachState.Connected) {
                    if (_outageSince is { } since) _outageEnded?.Invoke(_outageExpected, _time.GetUtcNow() - since);
                    _outageSince = null;
                    _outageReason = null;
                    _outageExpected = false;
                } else if (next.State == AttachState.Unreachable) {
                    _outageSince ??= _time.GetUtcNow();
                    _outageReason = next.Reason;
                }
                _raw = next;
            })));
    }

    /// The attach status the app shows: the service's own, relabelled while a restart is expected.
    public IObservable<AttachStatus> Status => _status.DistinctUntilChanged();

    /// Whether an outage right now would be one the daemon chose.
    public bool Expected {
        get {
            lock (_lock) return IsExpected();
        }
    }

    bool IsExpected() =>
        _raw is { State: AttachState.Unreachable or AttachState.Connecting } && _outageReason == UnreachableReason
        && _outageSince is { } since && _time.GetUtcNow() - since < Grace
        && (_marker || _appJustUpdated());

    // Published under the lock, so two sources updating at once cannot reach subscribers in the reverse order.
    void Update(Action change) {
        lock (_lock) {
            if (_disposed) return;
            change();
            if (_raw is null) return;
            var expected = IsExpected();
            _outageExpected |= expected;
            _timer.Change(expected ? RetryInterval : Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _status.OnNext(expected ? new AttachStatus(AttachState.Unreachable, Reason, null, _raw.DaemonVersion) : _raw);
        }
    }

    // Only from Unreachable: a retry cancels the attach in flight, and a daemon that is still handshaking must be
    // left to finish. The retry's own Unreachable re-arms the timer.
    void Tick() {
        bool retry;
        lock (_lock) {
            if (_disposed) return;
            retry = IsExpected() && _raw?.State == AttachState.Unreachable;
        }
        if (retry) _ = ReattachAsync();
        Update(() => { });
    }

    async Task ReattachAsync() {
        try {
            await _reattach().ConfigureAwait(false);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: reattach during daemon restart failed: {ex.Message}");
        }
    }

    public void Dispose() {
        lock (_lock) {
            if (_disposed) return;
            _disposed = true;
        }
        _subscription.Dispose();
        _timer.Dispose();
        _status.OnCompleted();
        _status.Dispose();
    }
}
