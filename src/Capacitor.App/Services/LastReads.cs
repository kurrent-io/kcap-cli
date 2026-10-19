namespace Capacitor.App.Services;

/// The last read worth showing per session, kept app-wide so a pane rebuilt for the session shows
/// it while it reads again. Clear runs when the credential changes: a read one identity was shown
/// must never seed a pane under another, including one still in flight across the change.
public sealed class LastReads<T>(int capacity = 64) where T : class {
    readonly Lock _lock = new();
    readonly OrderedDictionary<string, T> _reads = new(StringComparer.Ordinal);
    long _generation;

    public long Generation {
        get { lock (_lock) return _generation; }
    }

    public T? Get(string key) {
        lock (_lock) return _reads.GetValueOrDefault(key);
    }

    /// `remember`: true keeps the result, false drops what was kept, null leaves it as it was.
    public async Task<T> RecordAsync(string key, Func<Task<T>> read, Func<T, bool?> remember) {
        long generation;
        lock (_lock) generation = _generation;
        var result = await read().ConfigureAwait(false);
        switch (remember(result)) {
            case true: Set(key, result, generation); break;
            case false: Forget(key); break;
        }
        return result;
    }

    /// `generation` is Generation as read before the read began; a clear since then drops the value.
    public void Set(string key, T value, long generation) {
        lock (_lock) if (generation == _generation) Put(key, value);
    }

    public void Forget(string key) {
        lock (_lock) _reads.Remove(key);
    }

    public void Clear() {
        lock (_lock) {
            _generation++;
            _reads.Clear();
        }
    }

    void Put(string key, T value) {
        _reads.Remove(key);
        _reads.Add(key, value);
        while (_reads.Count > capacity) _reads.RemoveAt(0);
    }
}
