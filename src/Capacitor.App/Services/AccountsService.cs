using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.LocalIpc;

namespace Capacitor.App.Services;

/// Holds the connected daemon's accounts snapshot. An attempt that ends keeps the snapshot it
/// delivered: the next attempt resends the current one, so the held value is replaced rather than
/// blinked out. Only losing the connection or the capability clears it.
/// One lock guards the snapshot, the waiters, the loop token and the disposed flag, so a waiter
/// registered after a publish sees that snapshot and one registered before it is released by it.
public sealed class AccountsService : IAccountsService, IDisposable {
    internal const string Capability = "accounts/1";
    static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    sealed class Waiter(long revision) {
        public long Revision { get; } = revision;
        public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    readonly Lock _lock = new();
    readonly BehaviorSubject<AccountsSnapshotDto?> _snapshot = new(null);
    readonly BehaviorSubject<bool> _supported = new(false);
    readonly List<Waiter> _waiters = [];
    readonly Func<CancellationToken, IAsyncEnumerable<AccountsStreamEvent>> _subscribe;
    readonly TimeProvider _time;
    readonly CancellationToken _shutdownToken;
    readonly IDisposable _statusSub;
    CancellationTokenSource? _loopCts;
    bool _disposed;

    public AccountsService(
            IDaemonClientService service, Func<CancellationToken, IAsyncEnumerable<AccountsStreamEvent>> subscribe,
            TimeProvider time, CancellationToken shutdownToken) {
        _subscribe = subscribe; _time = time; _shutdownToken = shutdownToken;
        _statusSub = service.Status.Subscribe(OnStatus);
    }

    public IObservable<AccountsSnapshotDto?> Snapshot => _snapshot.AsObservable();
    public IObservable<bool> Supported => _supported.AsObservable();

    public async Task<bool> ReachedAsync(long revision, TimeSpan timeout, CancellationToken ct) {
        Waiter waiter;
        lock (_lock) {
            if (_snapshot.Value is { } held) {
                if (held.Revision >= revision) return true;
            } else if (_loopCts is null) {
                return false;
            }
            waiter = new Waiter(revision);
            _waiters.Add(waiter);
        }
        try {
            return await waiter.Completion.Task.WaitAsync(timeout, _time, ct).ConfigureAwait(false);
        } catch (TimeoutException) {
            return false;
        } finally {
            lock (_lock) _waiters.Remove(waiter);
        }
    }

    public void Dispose() {
        lock (_lock) {
            if (_disposed) return;
            _disposed = true;
        }
        _statusSub.Dispose();
        StopLoop();
    }

    void OnStatus(AttachStatus status) {
        if (status is { State: AttachState.Connected, Capabilities: not null } && status.Capabilities.Contains(Capability)) {
            SetSupported(true);
            StartLoop();
            return;
        }
        StopLoop();
        SetSupported(false);
    }

    void SetSupported(bool value) {
        lock (_lock) {
            if (_disposed || _supported.Value == value) return;
            _supported.OnNext(value);
        }
    }

    void StartLoop() {
        CancellationTokenSource cts;
        lock (_lock) {
            if (_disposed || _loopCts is not null) return;
            cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
            _loopCts = cts;
        }
        _ = Task.Run(() => RunLoopAsync(cts));
    }

    void StopLoop() {
        CancellationTokenSource? cts;
        lock (_lock) {
            cts = _loopCts;
            _loopCts = null;
            PublishLocked(null);
        }
        if (cts is null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    async Task RunLoopAsync(CancellationTokenSource cts) {
        var ct = cts.Token;
        try {
            while (!ct.IsCancellationRequested) {
                try {
                    await foreach (var evt in _subscribe(ct).WithCancellation(ct).ConfigureAwait(false)) {
                        ct.ThrowIfCancellationRequested();
                        if (evt is AccountsStreamEvent.Snapshot snapshot) Publish(cts, snapshot.Value);
                    }
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    break;
                } catch (Exception ex) {
                    Console.Error.WriteLine($"kcap: accounts subscription attempt failed: {ex.Message}");
                }
                try { await Task.Delay(RetryDelay, _time, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        } finally {
            cts.Dispose();
        }
    }

    /// A loop that has been stopped can still deliver the event it was reading; it must not
    /// repopulate the snapshot StopLoop cleared.
    void Publish(CancellationTokenSource cts, AccountsSnapshotDto snapshot) {
        lock (_lock) {
            if (!ReferenceEquals(_loopCts, cts)) return;
            PublishLocked(snapshot);
        }
    }

    // Caller holds _lock. A null snapshot releases every waiter as false: the subscription they
    // were waiting on is gone.
    void PublishLocked(AccountsSnapshotDto? snapshot) {
        if (!ReferenceEquals(_snapshot.Value, snapshot)) _snapshot.OnNext(snapshot);
        for (var i = _waiters.Count - 1; i >= 0; i--) {
            var waiter = _waiters[i];
            if (snapshot is not null && snapshot.Revision < waiter.Revision) continue;
            waiter.Completion.TrySetResult(snapshot is not null);
            _waiters.RemoveAt(i);
        }
    }
}
