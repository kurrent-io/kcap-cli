using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.App.ViewModels;

/// The transcript file, tailed and projected line by line. The projection context lives exactly as
/// long as the file the tail is reading: a reset discards it with the line count.
internal sealed class LocalTranscriptFeed(
        string path, IChatTranscriptProjection projection, string agentId, TimeProvider time, Action<string> logOnce)
    : IChatTranscriptFeed {
    readonly JsonlTail _tail = new(path);
    TranscriptContext? _context;
    int _linesRead;
    /// Subagents whose starting call has been reported since the file was last read from its start.
    readonly HashSet<string> _started = new(StringComparer.Ordinal);

    public long? CurrentOffset {
        get {
            try { return new FileInfo(path).Length; }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
    }

    public FeedRead ReadAppended() {
        var result = _tail.ReadAppended();
        if (result.Status == TailStatus.Reset) { _context = null; _linesRead = 0; _started.Clear(); }
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
        // The chat applies a read's run signals only when it applies its lines, so only those reads report them.
        return new(status, lines, snapshot, result.Failure) { Runs = status is FeedStatus.Ok or FeedStatus.Reset ? SubagentStarts() : [] };
    }

    // A running foreground subagent is named nowhere in the parent's file until its result arrives.
    IReadOnlyList<RunSignal> SubagentStarts() {
        var starts = ClaudeSubagentFiles.Starts(path, _started);
        if (starts.Count == 0) return [];
        var runs = new List<RunSignal>(starts.Count);
        foreach (var (toolUseId, agentId) in starts) {
            _started.Add(agentId);
            runs.Add(new RunSignal.Identified(toolUseId, agentId));
        }
        return runs;
    }

    public void Dispose() { }
}
