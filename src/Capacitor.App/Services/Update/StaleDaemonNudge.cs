using System.Reactive.Linq;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Services.Update;

/// <summary>
/// After an update, asks a daemon still connected on an older version, with no restart queued, to restart once it
/// has no agents. A daemon restarts itself when its binary changes, but one that started while the update was
/// replacing that binary can miss the change, and stays on the old version until something restarts it. It is
/// asked once: a daemon that comes back still older runs a binary this app does not ship.
/// </summary>
public sealed class StaleDaemonNudge : IDisposable {
    /// Past the daemon's own 15 s binary poll, so one that noticed the change has queued its restart by then.
    internal static readonly TimeSpan Grace = TimeSpan.FromSeconds(20);
    internal const string Mode = "when-idle";

    readonly Lock _lock = new();
    readonly Func<CancellationToken, Task<string>> _request;
    readonly Action<string> _outcome;
    readonly TimeProvider _time;
    readonly CancellationToken _ct;
    readonly IDisposable _subscription;
    ITimer? _timer;
    // A disposed timer can still run a callback it already queued: the generation tells a stale one to do nothing.
    long _generation;
    bool _asked;
    bool _disposed;

    /// <param name="request">Sends the restart request and returns the daemon's ack.</param>
    /// <param name="outcome">Told the ack, or why the request failed.</param>
    public StaleDaemonNudge(
            string appVersion, IObservable<AttachStatus> status, IObservable<string?> daemonVersion, IObservable<bool> restartPending,
            Func<CancellationToken, Task<string>> request, Action<string> outcome, TimeProvider time, CancellationToken ct) {
        _request = request;
        _outcome = outcome;
        _time = time;
        _ct = ct;
        _subscription = status.CombineLatest(daemonVersion, restartPending,
                (s, version, pending) => s.State == AttachState.Connected && !pending && Behind(version, appVersion))
            .DistinctUntilChanged()
            .Subscribe(Arm);
    }

    internal static bool Behind(string? daemonVersion, string appVersion) =>
        daemonVersion is { Length: > 0 } && PrereleaseSemver.IsNewer(Core(appVersion), Core(daemonVersion));

    static string Core(string version) => version.Split('+', 2)[0];

    void Arm(bool stale) {
        lock (_lock) {
            var generation = ++_generation;
            _timer?.Dispose();
            _timer = stale && !_asked && !_disposed
                ? _time.CreateTimer(_ => Fire(generation), null, Grace, Timeout.InfiniteTimeSpan)
                : null;
        }
    }

    void Fire(long generation) {
        lock (_lock) {
            if (_asked || _disposed || generation != _generation) return;
            _asked = true;
        }
        _ = SendAsync();
    }

    async Task SendAsync() {
        string result;
        try {
            result = await _request(_ct).ConfigureAwait(false);
        } catch (OperationCanceledException) when (_ct.IsCancellationRequested) {
            return;
        } catch (Exception ex) {
            result = ex is LocalControlOpsException ops ? ops.Reason : ex.GetType().Name;
        }
        _outcome(result);
    }

    public void Dispose() {
        lock (_lock) {
            _disposed = true;
            _generation++;
            _timer?.Dispose();
            _timer = null;
        }
        _subscription.Dispose();
    }
}
