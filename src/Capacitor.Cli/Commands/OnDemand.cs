namespace Capacitor.Cli.Commands;

/// <summary>
/// A value created on first use and kept once created. A failed creation leaves nothing behind, so the next
/// caller retries rather than inheriting the fault; concurrent first callers share one creation.
/// </summary>
sealed class OnDemand<T>(Func<Task<T>> create) : IDisposable where T : class {
    readonly SemaphoreSlim _gate = new(1, 1);
    T? _value;

    public async Task<T> GetAsync() {
        if (Volatile.Read(ref _value) is { } created) return created;

        await _gate.WaitAsync();
        try {
            if (_value is { } raced) return raced;
            var value = await create();
            Volatile.Write(ref _value, value);
            return value;
        } finally {
            _gate.Release();
        }
    }

    public void Dispose() {
        try {
            (_value as IDisposable)?.Dispose();
        } catch {
            // Best-effort cleanup at shutdown.
        }
        _gate.Dispose();
    }
}
