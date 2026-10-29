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

    /// Applies one decision per session to every row the read numbered <paramref name="readSeq"/>
    /// returned, whichever set lists it: the entry holds while any copy still carries a title it
    /// replaced, and a read that settles it retires it for all of them.
    public IEnumerable<T> Resolve<T>(IReadOnlyList<T> rows, Func<T, string?> sessionId, Func<T, string?> title,
            Func<T, string, T> retitle, long readSeq) {
        if (_entries.Count == 0) return rows;
        var held = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in rows.GroupBy(r => Key(sessionId(r)))) {
            if (group.Key is not { } key || !_entries.TryGetValue(key, out var entry)) continue;
            var listed = group.Select(title).Where(t => t is { Length: > 0 }).ToList();
            var settled = readSeq > entry.ReadSeq && listed.Count > 0
                && listed.All(t => t == entry.Title || !entry.Replaced.Contains(t!));
            if (settled || time.GetUtcNow() - entry.RecordedAt >= Hold) _entries.Remove(key);
            else held[key] = entry.Title;
        }
        return rows.Select(r => Key(sessionId(r)) is { } key && held.TryGetValue(key, out var shown) && shown != title(r)
            ? retitle(r, shown) : r).ToList();
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
