namespace Capacitor.Cli.Core.Skills;

/// <summary>The paths a sync still owes a deletion. Kept apart from the document-keyed entries
/// because a rename — one document, two paths — cannot be recorded in a ledger with one row per
/// document, and a crash between the write and the prune would otherwise leave the old path with
/// nothing owning it.</summary>
public static class SkillsJournal {
    public static PendingPrune[] Merge(IReadOnlyList<PendingPrune>? existing, IEnumerable<PendingPrune> added) =>
        [.. (existing ?? []).Concat(added)
            .GroupBy(p => CanonicalPath.Resolve(p.Path), PathComparison.Comparer)
            .Select(g => g.First())];

    /// <summary>Drops the intents whose path the new plan writes again: a document renamed away and
    /// back leaves an intent to delete the very directory that is about to be published.</summary>
    public static PendingPrune[] Reconcile(IReadOnlyList<PendingPrune> journal, IEnumerable<string> liveDestinations) {
        var live = liveDestinations.Select(CanonicalPath.Resolve).ToHashSet(PathComparison.Comparer);
        return [.. journal.Where(p => !live.Contains(CanonicalPath.Resolve(p.Path)))];
    }
}
