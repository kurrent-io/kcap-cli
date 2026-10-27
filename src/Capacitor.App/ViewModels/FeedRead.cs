using Capacitor.Cli.Core;

namespace Capacitor.App.ViewModels;

/// What one poll drained. A Reset carries the rebuilt source's replayed lines and SnapshotOffset
/// names where that history ends, so a send is rebased past the replay yet can still be
/// acknowledged by anything that arrived after it.
public sealed record FeedRead(FeedStatus Status, IReadOnlyList<ProjectedLine> Lines, long? SnapshotOffset = null, string? Failure = null) {
    /// Run signals the feed read outside its lines, applied after them.
    public IReadOnlyList<RunSignal> Runs { get; init; } = [];
}
