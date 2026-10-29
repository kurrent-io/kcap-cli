using Avalonia.Threading;

namespace Capacitor.App.ViewModels;

/// Single-flight async reader keyed by a lease state. A result applies on the UI thread only when
/// its lease is still current; superseded and torn-down leases are retained until their tasks
/// complete so teardown can cancel and, by default, await every read. Each lease disposes its
/// CancellationTokenSource after it settles and is released. A new lease whose state has a cached
/// result applies it at once, settled, and still reads.
internal sealed class SingleFlightReader<TState, TResult>(
        Func<TState, CancellationToken, Task<TResult>> readAsync,
        Func<Exception, TResult> onReadError,
        Action<TResult> apply,
        string logTag,
        Action<bool>? onReadingChanged = null,
        Action? onSettled = null,
        Func<TState, TResult?>? cached = null)
        where TResult : class {
    sealed class Lease(TState state) {
        public TState State { get; } = state;
        public CancellationTokenSource Cts { get; } = new();
        public Task? Pending;
        public bool RefreshPending;
        public bool Settled;
        public bool IsReading;
    }

    readonly List<Lease> _outstanding = [];
    Lease? _current;
    bool _tornDown;

    public bool HasCurrent => _current is not null;
    public TState? CurrentState => _current is { } lease ? lease.State : default;
    public bool IsCurrentSettled => _current is { Settled: true };
    public bool IsLoading => _current is { Settled: false };
    public Task? PendingReadForTesting => _current?.Pending ?? _outstanding.LastOrDefault()?.Pending;

    public void Switch(TState state, Action? beforeStart = null) {
        if (_tornDown) return;
        var old = _current;
        var lease = _current = new Lease(state);
        Release(old);
        beforeStart?.Invoke();
        if (cached?.Invoke(state) is { } seed) {
            lease.Settled = true;
            apply(seed);
            onSettled?.Invoke();
            if (!IsLive(lease)) return;
        }
        StartRead(lease);
    }

    public void ClearCurrent(Action? afterClear = null) {
        var old = _current;
        _current = null;
        Release(old);
        afterClear?.Invoke();
    }

    static void Release(Lease? lease) {
        if (lease is null) return;
        lease.Cts.Cancel();
        if (!lease.IsReading) lease.Cts.Dispose();
    }

    public void Refresh() {
        if (_tornDown || _current is not { } lease) return;
        if (lease.IsReading) lease.RefreshPending = true;
        else StartRead(lease);
    }

    public void StartIfIdle() {
        if (!_tornDown && _current is { IsReading: false } lease) StartRead(lease);
    }

    void StartRead(Lease lease) {
        lease.RefreshPending = false;
        lease.IsReading = true;
        lease.Pending = RunReadAsync(lease);
        _outstanding.Add(lease);
        if (ReferenceEquals(lease, _current)) onReadingChanged?.Invoke(true);
    }

    async Task RunReadAsync(Lease lease) {
        TResult? result = null;
        try {
            result = await readAsync(lease.State, lease.Cts.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
        } catch (Exception ex) {
            result = onReadError(ex);
        }
        try {
            await Dispatcher.UIThread.InvokeAsync(() => Settle(lease, result));
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: {logTag}: {ex.Message}");
        }
    }

    void Settle(Lease lease, TResult? result) {
        lease.IsReading = false;
        _outstanding.Remove(lease);
        if (!IsLive(lease)) { lease.Cts.Dispose(); return; }
        try {
            lease.Settled = true;
            if (result is not null) apply(result);
            onSettled?.Invoke();
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: {logTag}: {ex.Message}");
        }
        // The callbacks may switch, clear, refresh or tear down this reader; a released lease's
        // source is already disposed and must never be listed again.
        if (!IsLive(lease) || lease.IsReading) return;
        if (lease.RefreshPending) { StartRead(lease); return; }
        onReadingChanged?.Invoke(false);
    }

    bool IsLive(Lease lease) => ReferenceEquals(lease, _current) && !_tornDown;

    public async Task TeardownAsync(bool waitForPending = true) {
        if (_tornDown) return;
        _tornDown = true;
        var old = _current;
        _current = null;
        Release(old);
        var leases = _outstanding.ToArray();
        foreach (var lease in leases) lease.Cts.Cancel();
        // Uninterruptible filesystem reads settle and dispose their leases after teardown returns.
        if (!waitForPending) return;
        foreach (var lease in leases)
            if (lease.Pending is { } pending) {
                try { await pending; } catch (Exception) { }
            }
        foreach (var lease in leases) lease.Cts.Dispose();
    }
}
