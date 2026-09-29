using Capacitor.Cli.Core.Harness.Titles;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Core.Commands;

/// <summary>Decides when a harness store's title is posted and with what change time. A change the store does not
/// time is timed by the last read that still showed the old value — a lower bound, so an error favours a Capacitor
/// Regenerate — and the first such read after start is untimed.</summary>
internal sealed class HarnessTitleTracker(bool recordsChangeTime) {
    StoreTitle?       _lastRead;
    DateTimeOffset?   _lastReadAt;
    HarnessTitlePost? _pending;
    HarnessTitlePost? _settled;

    /// <summary>The post to send for this read, or null when nothing is owed. An unsettled value comes back as the
    /// same instance on every read, with the time it was first given.</summary>
    public HarnessTitlePost? Observe(StoreTitle? read, DateTimeOffset now) {
        if (read is null) return null;

        var previous   = _lastRead;
        var previousAt = _lastReadAt;
        _lastRead   = read;
        _lastReadAt = now;

        if (Same(_settled, read)) {
            _pending = null;
            return null;
        }

        if (Same(_pending, read)) return _pending;

        DateTimeOffset? changedAt = recordsChangeTime && read.RecordedChangeAt is { } recorded
            ? recorded
            : previous is not null && (previous.Title != read.Title || previous.Kind != read.Kind) ? previousAt : null;

        return _pending = new HarnessTitlePost(read.Title, read.Kind, changedAt);
    }

    /// <summary>The server has answered for this value — recorded it, or refused it on its merits — so it is not
    /// sent again until the store's value changes.</summary>
    public void Settled(HarnessTitlePost post) {
        _settled = post;
        _pending = null;
    }

    static bool Same(HarnessTitlePost? post, StoreTitle read) => post is not null && post.Title == read.Title && post.Kind == read.Kind;
}
