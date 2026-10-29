using Capacitor.Cli.Core.WorkItems;

namespace Capacitor.App.Services;

/// Applied renames by canonical session id, shown over a listing that has not caught up yet. Every
/// title an unsettled rename replaced counts as lag, so only a read started after the latest rename
/// that carries some other title, or the hold running out, retires an entry. Not thread-safe: the
/// owner calls it under its own lock.
public sealed class SessionTitleOverlay(TimeProvider time) {
    /// The server's title sync lags the write by seconds; the bound stops a server that kept an
    /// older title from being masked forever.
    static readonly TimeSpan Hold = TimeSpan.FromMinutes(2);

    sealed record Entry(string Title, HashSet<string> Replaced, long ReadSeq, DateTimeOffset RecordedAt);

    readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    long _readSeq;

    /// Called when a listing read starts; its result is resolved against the number returned.
    public long BeginRead() => ++_readSeq;

    /// The title to show for a row that the read numbered <paramref name="readSeq"/> listed with
    /// <paramref name="listed"/>; an entry that read settles is retired.
    public string? Resolve(string? sessionId, string? listed, long readSeq) {
        if (Key(sessionId) is not { } key || !_entries.TryGetValue(key, out var entry)) return listed;
        var settled = readSeq > entry.ReadSeq && listed is { Length: > 0 }
            && (listed == entry.Title || !entry.Replaced.Contains(listed));
        if (settled || time.GetUtcNow() - entry.RecordedAt >= Hold) {
            _entries.Remove(key);
            return listed;
        }
        return entry.Title;
    }

    /// Records an applied rename; <paramref name="listed"/> is the title the listing shows for the
    /// session now, which a lagging read can still carry.
    public void Record(string sessionId, string shown, string? listed) {
        if (Key(sessionId) is not { } key) return;
        var replaced = new HashSet<string>(StringComparer.Ordinal);
        if (_entries.TryGetValue(key, out var prior)) {
            replaced.UnionWith(prior.Replaced);
            replaced.Add(prior.Title);
        } else if (listed is { Length: > 0 }) {
            replaced.Add(listed);
        }
        _entries[key] = new(shown, replaced, _readSeq, time.GetUtcNow());
    }

    public void Clear() => _entries.Clear();

    public static string? Key(string? sessionId) => WorkContextIds.CanonicalSessionId(sessionId);
}
