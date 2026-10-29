namespace Capacitor.Cli.Harness.MuseAssistant;

/// <summary>A validated walk: every item of every page, ascending by seq, plus what the oldest page says
/// about where the walk stopped.</summary>
public sealed record MuseAssistantWalk(
        string                           ChatId,
        bool                             IsMain,
        long                             FetchedAtMs,
        IReadOnlyList<MuseAssistantItem> Items,
        bool                             OldestHasMore,
        long?                            OldestPageMinSeq
    );
