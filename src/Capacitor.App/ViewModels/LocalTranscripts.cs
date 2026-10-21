using Capacitor.Cli.Core;

namespace Capacitor.App.ViewModels;

/// Each local transcript tailed and projected once for the app, so a chat pane opened again
/// replays the rows already projected instead of reading the file from its start.
// ponytail: whole projected history held per transcript, last 16 kept; trim to a tail window if memory shows.
public sealed class LocalTranscripts(int capacity = 16) {
    readonly Lock _lock = new();
    readonly OrderedDictionary<string, Shared> _open = new(StringComparer.Ordinal);

    internal IChatTranscriptFeed Open(string path, IChatTranscriptProjection projection, string agentId, TimeProvider time, Action<string> log) {
        var key = string.Join('\n', path, agentId, projection.GetType().FullName);
        Shared shared;
        lock (_lock) {
            if (_open.Remove(key, out var known)) shared = known;
            else shared = new Shared(new LocalTranscriptFeed(path, projection, agentId, time, log));
            _open.Add(key, shared);
            // An evicted transcript stays readable by the panes already holding it.
            while (_open.Count > capacity) _open.RemoveAt(0);
        }
        return new Reader(shared);
    }

    /// Reads run on worker threads, and a pane being torn down can still be reading beside its
    /// successor, so every read of the tail and its history holds the lock.
    internal sealed class Shared(LocalTranscriptFeed feed) {
        public readonly Lock Lock = new();
        public readonly LocalTranscriptFeed Feed = feed;
        public readonly List<ProjectedLine> History = [];
        public int Epoch;
        public long? Snapshot;
    }

    /// A reader's first read is the whole history as Ok, as a cold tail's would be; a reset after
    /// that replays it as Reset.
    internal sealed class Reader(Shared shared) : IChatTranscriptFeed {
        bool _started;
        int _epoch;
        int _cursor;

        public long? CurrentOffset => shared.Feed.CurrentOffset;

        public FeedRead ReadAppended() {
            lock (shared.Lock) {
                var read = shared.Feed.ReadAppended();
                switch (read.Status) {
                    case FeedStatus.Reset:
                        shared.History.Clear();
                        shared.History.AddRange(read.Lines);
                        shared.Epoch++;
                        break;
                    case FeedStatus.Ok:
                        shared.History.AddRange(read.Lines);
                        break;
                    default:
                        return read;
                }
                shared.Snapshot = read.SnapshotOffset ?? shared.Snapshot;
                var replay = _started && _epoch != shared.Epoch;
                if (!_started || replay) {
                    _started = true;
                    _epoch = shared.Epoch;
                    _cursor = 0;
                }
                var lines = shared.History.GetRange(_cursor, shared.History.Count - _cursor);
                _cursor = shared.History.Count;
                return new(replay ? FeedStatus.Reset : FeedStatus.Ok, lines, shared.Snapshot);
            }
        }

        public void Dispose() { }
    }
}
