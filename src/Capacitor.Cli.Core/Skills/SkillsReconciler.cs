namespace Capacitor.Cli.Core.Skills;

public sealed record PlannedSkillWrite(SkillSnapshotItem Item, SkillDestination At);

public sealed record RefusedSkillWrite(SkillSnapshotItem Item, string Destination, string Reason);

public sealed record SkillsSyncPlan(
    IReadOnlyList<PlannedSkillWrite> Writes,
    IReadOnlyList<SkillSnapshotItem> Unchanged,
    IReadOnlyList<RefusedSkillWrite> Refusals);

/// <summary>
/// Reconciles the rows against a fresh snapshot. Identity is <c>doc_id</c> — the server's stable
/// key — so a retitled doc is a write at the new path plus a deletion of the old one, and the
/// destination a document belongs at is derived from its slug rather than read back off a row.
/// </summary>
public static class SkillsReconciler {
    public static SkillsSyncPlan Plan(
            OwnedSkillRows rows, IReadOnlyList<SkillSnapshotItem> snapshot, SkillsTarget target,
            string anchor, IReadOnlySet<Guid> withheld) {
        List<PlannedSkillWrite> writes    = [];
        List<SkillSnapshotItem> unchanged = [];
        List<RefusedSkillWrite> refusals  = [];

        foreach (var item in snapshot) {
            if (withheld.Contains(item.DocId)) continue;

            var at  = SkillDestination.For(target, anchor, item.Slug);
            var row = rows.At(at.Path);

            if (row is null) {
                // A destination that already exists and no row names is the repository's own, and
                // may be tracked. An intended hash is never a reason to take it over, and a
                // destination that would not answer is not one to claim either.
                var free = PathExistence.OfDirectory(at.Path);

                if (free == PathPresence.Missing) writes.Add(new PlannedSkillWrite(item, at));
                else
                    refusals.Add(new RefusedSkillWrite(item, at.Path, free == PathPresence.Present
                        ? "the directory already exists and kcap does not own it"
                        : "the directory could not be established as free"));

                continue;
            }

            if (Refuse(row, item) is { } reason) {
                refusals.Add(new RefusedSkillWrite(item, at.Path, reason));
                continue;
            }

            if (Serves(row, item, anchor)) unchanged.Add(item); else writes.Add(new PlannedSkillWrite(item, at));
        }

        return new SkillsSyncPlan(writes, unchanged, refusals);
    }

    /// <summary>Whether this row already satisfies publication here: ours, verified, at this
    /// anchor, and holding on disk exactly the bytes it confirmed writing.</summary>
    public static bool Serves(OwnedSkillRow row, SkillSnapshotItem item, string anchor) =>
        row is { State: OwnedSkillState.Published, Origin: SkillOrigin.Repository, Confirmed: { } confirmed }
        && row.Prepared is null
        && row.Anchor is { } recorded
        && PathComparison.Equal(CanonicalPath.Resolve(recorded), CanonicalPath.Resolve(anchor))
        && confirmed.Document.Serves(item)
        && SkillsMaterializer.Inspect(row.Path) is (SkillFileProbe.Present, { } hash)
        && confirmed.Matches(hash);

    static string? Refuse(OwnedSkillRow row, SkillSnapshotItem item) =>
        row.State == OwnedSkillState.Settled
            ? "kcap holds no claim on that directory"
        : row is { State: OwnedSkillState.Owed, Cause: SkillDeletionCause.Retired }
            ? "a retired account's deletion is still owed there"
        : row.Document is { } document && document.DocId != item.DocId
            ? "another document is recorded at that path"
        : null;
}
