namespace Capacitor.Cli.Daemon.Services;

/// <summary>One async lock per key, held across awaits; holders of different keys never wait on
/// each other. A key's lock is dropped once nobody holds or waits on it.</summary>
internal sealed class KeyedAsyncGate {
    sealed class Entry : IDisposable {
        public readonly SemaphoreSlim Semaphore = new(1, 1);
        public int Users;

        public void Dispose() => Semaphore.Dispose();
    }

    readonly Lock _lock = new();
    readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public async Task<IDisposable> EnterAsync(string key, CancellationToken ct) {
        Entry entry;
        lock (_lock) {
            if (!_entries.TryGetValue(key, out entry!)) _entries[key] = entry = new Entry();
            entry.Users++;
        }
        try {
            await entry.Semaphore.WaitAsync(ct).ConfigureAwait(false);
        } catch {
            Leave(key, entry, release: false);
            throw;
        }
        return new Releaser(this, key, entry);
    }

    void Leave(string key, Entry entry, bool release) {
        lock (_lock) {
            if (release) entry.Semaphore.Release();
            if (--entry.Users == 0) {
                _entries.Remove(key);
                entry.Dispose();
            }
        }
    }

    sealed class Releaser(KeyedAsyncGate gate, string key, Entry entry) : IDisposable {
        int _disposed;

        public void Dispose() {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) gate.Leave(key, entry, release: true);
        }
    }
}
