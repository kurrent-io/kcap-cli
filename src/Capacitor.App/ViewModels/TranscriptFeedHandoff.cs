namespace Capacitor.App.ViewModels;

/// A closing workspace's transcript feed, positioned where its chat stopped, and the runs it folded so far:
/// the monitor that takes over carries on from there instead of reading the transcript again from the start.
public sealed record TranscriptFeedHandoff(IChatTranscriptFeed Feed, string Key, SessionRuns Runs);
