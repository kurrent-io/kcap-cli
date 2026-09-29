using Capacitor.Cli.Core.Harness.Titles;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Core.Commands;

/// <summary>Decides when a harness store's title is posted and with what change time. Without a recorded change
/// time a change is timed by the last read that still showed the old value — a lower bound, so an error favours a
/// Capacitor Regenerate — and the first read after start is untimed.</summary>
internal sealed class HarnessTitleTracker(bool recordsChangeTime) {
    StoreTitle?       _lastRead;
    DateTimeOffset?   _lastReadAt;
    HarnessTitlePost? _pending;
    HarnessTitlePost? _lastPosted;

    public HarnessTitlePost? Observe(StoreTitle? read, DateTimeOffset now) {
        if (read is null) return null;

        var previous   = _lastRead;
        var previousAt = _lastReadAt;
        _lastRead   = read;
        _lastReadAt = now;

        if (Same(_lastPosted, read)) return null;

        // An unposted value keeps the time it was first given, so a retry after a failed post is not re-timed.
        if (Same(_pending, read)) return _pending;

        DateTimeOffset? changedAt = recordsChangeTime
            ? read.RecordedChangeAt
            : previous is not null && (previous.Title != read.Title || previous.Kind != read.Kind) ? previousAt : null;

        return _pending = new HarnessTitlePost(read.Title, read.Kind, changedAt);
    }

    public void Posted(HarnessTitlePost post) {
        _lastPosted = post;
        _pending    = null;
    }

    static bool Same(HarnessTitlePost? post, StoreTitle read) => post is not null && post.Title == read.Title && post.Kind == read.Kind;
}
