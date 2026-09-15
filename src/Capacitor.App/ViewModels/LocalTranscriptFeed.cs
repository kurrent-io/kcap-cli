using Capacitor.Cli.Core;

namespace Capacitor.App.ViewModels;

/// The transcript file, tailed and projected line by line. The projection context lives exactly as
/// long as the file the tail is reading: a reset discards it with the line count.
internal sealed class LocalTranscriptFeed(
        string path, IChatTranscriptProjection projection, string agentId, TimeProvider time, Action<string> logOnce)
    : IChatTranscriptFeed {
    readonly JsonlTail _tail = new(path);
    TranscriptContext? _context;
    int _linesRead;

    public long? CurrentOffset {
        get {
            try { return new FileInfo(path).Length; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }

    public FeedRead ReadAppended() {
        var result = _tail.ReadAppended();
        if (result.Status == TailStatus.Reset) { _context = null; _linesRead = 0; }
        var lines = new List<ProjectedLine>(result.Lines.Count);
        if (result.Lines.Count > 0) {
            // The app has no session id and persists nothing, so the agent id stands in; only
            // attachment ids would read it.
            _context ??= projection.CreateContext(agentId, null);
            _context.BeginBatch();
            var receivedAt = time.GetUtcNow();
            for (var index = 0; index < result.Lines.Count; index++) {
                // Counts the lines the tail yields, which skips blank lines, so it is not the
                // file's physical line number.
                var lineNumber = ++_linesRead;
                try {
                    lines.Add(new(projection.ProjectWithInputs(result.Lines[index], lineNumber, receivedAt, _context), result.LineStartOffsets[index]));
                } catch (Exception ex) {
                    logOnce($"projection: {ex.Message}");
                }
            }
        }
        var status = result.Status switch {
            TailStatus.Reset => FeedStatus.Reset,
            TailStatus.Missing => FeedStatus.Missing,
            TailStatus.Failed => FeedStatus.Failed,
            _ => FeedStatus.Ok,
        };
        // A reset's boundary is the file as it stands once projection is done, so an append that
        // landed during the read counts as replayed history rather than a receipt.
        var snapshot = status == FeedStatus.Reset
            ? Math.Max(result.SnapshotLength ?? 0, CurrentOffset ?? 0)
            : result.SnapshotLength;
        return new(status, lines, snapshot, result.Failure);
    }

    public void Dispose() { }
}
