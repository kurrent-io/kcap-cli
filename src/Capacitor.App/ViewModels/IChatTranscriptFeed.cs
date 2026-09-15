using Capacitor.Cli.Core;

namespace Capacitor.App.ViewModels;

public enum FeedStatus { Ok, Reset, Missing, Failed }

/// One projected transcript line and where it sits in its source: a byte offset in a file, an
/// event number in a stream. Offsets only ever grow within one source, which is what lets a send
/// be acknowledged by a line that landed after it.
public readonly record struct ProjectedLine(ChatProjectionResult Projection, long Offset);

/// What one poll drained. A Reset carries every line of the rebuilt source; SnapshotOffset is
/// where the source stood as the read began, so a send made against the previous source is
/// rebased past everything the new one replays.
public sealed record FeedRead(FeedStatus Status, IReadOnlyList<ProjectedLine> Lines, long? SnapshotOffset = null, string? Failure = null);

/// The chat pane's source of rows, drained from a worker thread on every poll.
public interface IChatTranscriptFeed : IDisposable {
    FeedRead ReadAppended();
    /// Where a send made now would be acknowledged from; null while the source has no position.
    long? CurrentOffset { get; }
}
