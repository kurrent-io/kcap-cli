namespace Capacitor.App.ViewModels;

/// The chat pane's source of rows, drained from a worker thread on every poll. Dispose may run
/// while a ReadAppended is still in flight — a switch or a teardown drops the feed without waiting
/// for the read — so an implementation must tolerate that and let the abandoned read return or
/// throw nothing the pane cannot absorb.
public interface IChatTranscriptFeed : IDisposable {
    FeedRead ReadAppended();
    /// Where a send made now would be acknowledged from; null while the source has no position.
    long? CurrentOffset { get; }
}
