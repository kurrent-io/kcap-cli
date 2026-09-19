namespace Capacitor.Cli.Commands;

/// <summary>Newest-first comparators. Dispatch order is a priority, not a landing order: chains
/// and routed sessions still run in two phases and four workers finish out of order.</summary>
internal static class ImportOrdering {
    public static DateTimeOffset CandidateTimestamp(ImportCommand.SessionClassification c) =>
        ImportCommand.ChainTimestamp(c);

    /// <summary>Descending timestamp, ties descending session id; an unresolvable timestamp
    /// (<see cref="DateTimeOffset.MinValue"/>) sorts last, then by the same key.</summary>
    public static IComparer<ImportCommand.SessionClassification> Candidate { get; } =
        Comparer<ImportCommand.SessionClassification>.Create((x, y) => CompareKeys(
            (CandidateTimestamp(x), x.SessionId), (CandidateTimestamp(y), y.SessionId)));

    public static IComparer<ImportCommand.SessionClassification> RoutedDispatch => Candidate;

    /// <summary>Chains compare by their max member timestamp, ties by their max session id.</summary>
    public static IComparer<List<ImportCommand.SessionClassification>> ChainDispatch { get; } =
        Comparer<List<ImportCommand.SessionClassification>>.Create((x, y) => CompareKeys(ChainKey(x), ChainKey(y)));

    static (DateTimeOffset, string) ChainKey(List<ImportCommand.SessionClassification> chain) =>
        (chain.Max(CandidateTimestamp), chain.Select(c => c.SessionId).Aggregate((a, b) => string.CompareOrdinal(a, b) >= 0 ? a : b));

    static int CompareKeys((DateTimeOffset Ts, string Id) a, (DateTimeOffset Ts, string Id) b) {
        var aUnknown = a.Ts == DateTimeOffset.MinValue;
        var bUnknown = b.Ts == DateTimeOffset.MinValue;
        if (aUnknown != bUnknown) return aUnknown ? 1 : -1;

        var byTs = b.Ts.CompareTo(a.Ts);
        return byTs != 0 ? byTs : string.CompareOrdinal(b.Id, a.Id);
    }
}
