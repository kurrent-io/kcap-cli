using Capacitor.Cli.Core;

namespace Capacitor.Cli.Daemon.Services;

/// One writer per journal path at a time, process-wide. FileStream has no cross-handle append, so two
/// handles on one file overwrite each other; the lock is what makes "one handle" true. Entries are
/// reference-counted so a waiter can never be left holding an entry the map has forgotten.
internal sealed class JournalPathLocks {
    public static readonly JournalPathLocks Shared = new();

    sealed class Entry : IDisposable {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public int Count;
        public void Dispose() => Gate.Dispose();
    }

    readonly Dictionary<string, Entry> _entries = new(PlatformPaths.Comparer);
    readonly Lock _sync = new();

    public int LiveEntries { get { lock (_sync) return _entries.Count; } }

    public async Task<IDisposable?> AcquireAsync(string path, TimeSpan timeout, CancellationToken ct) {
        var key = Path.GetFullPath(path);
        Entry entry;
        lock (_sync) {
            if (!_entries.TryGetValue(key, out entry!)) _entries[key] = entry = new Entry();
            entry.Count++;
        }
        var acquired = false;
        try {
            acquired = await entry.Gate.WaitAsync(timeout, ct).ConfigureAwait(false);
        } finally {
            if (!acquired) Decrement(key, entry);
        }
        return acquired ? new Lease(this, key, entry) : null;
    }

    void Decrement(string key, Entry entry) {
        lock (_sync) {
            if (--entry.Count > 0) return;
            _entries.Remove(key);
        }
        entry.Dispose();
    }

    sealed class Lease(JournalPathLocks owner, string key, Entry entry) : IDisposable {
        int _disposed;
        public void Dispose() {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            entry.Gate.Release();
            owner.Decrement(key, entry);
        }
    }
}
