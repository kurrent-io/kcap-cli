namespace Capacitor.Cli.Core.Skills;

public enum SkillRelocationOutcome {
    /// <summary>The old path holds what the row confirmed writing: the anchor changed without the
    /// files moving. The row goes on owning the old path and the plan materializes fresh at the new
    /// one.</summary>
    Anchored,

    /// <summary>Positively absent at the old path, matching at the new one: the copy travelled with
    /// the checkout.</summary>
    Moved,

    /// <summary>Present but edited, unreadable, or a path that will not resolve — each a different
    /// outcome, and none of them absence. The row keeps its tuple and is reported.</summary>
    Unproven,

    /// <summary>Absent at both ends. Nothing to relocate and nothing to report: whatever the row
    /// still owes, its deletion will find the path already empty.</summary>
    Gone,
}

public sealed record SkillRelocation(OwnedSkillRow Row, SkillRelocationOutcome Outcome);

/// <summary>
/// A checkout can be renamed or moved with its files, and the ledger lives inside that worktree's
/// git directory, so it travels with them. Content alone is not provenance: a vanished source is
/// what distinguishes a move from a copy, so relocation needs both sides.
/// </summary>
public static class SkillsRelocation {
    /// <summary>Every row with a physical copy is a candidate — an owed row owns a copy too, and a
    /// rename interrupted between the write and the prune leaves two. A settled row never is: it
    /// holds no claim, and relocation must not give it one.</summary>
    public static bool IsCandidate(OwnedSkillRow row, string anchor) =>
        row.Origin == SkillOrigin.Repository
        && row.Confirmed is not null
        && row.State is OwnedSkillState.Published or OwnedSkillState.Owed
        && row.Anchor is { } recorded
        && !PathComparison.Equal(CanonicalPath.Resolve(recorded), CanonicalPath.Resolve(anchor));

    public static SkillRelocation Resolve(OwnedSkillRow row, SkillDestination here) {
        var (probe, hash) = SkillsMaterializer.Inspect(row.Path);

        if (probe is SkillFileProbe.Present)
            return new SkillRelocation(row, row.Receipts.Any(r => r.Matches(hash!))
                ? SkillRelocationOutcome.Anchored
                : SkillRelocationOutcome.Unproven);

        if (probe is not SkillFileProbe.Absent) return new SkillRelocation(row, SkillRelocationOutcome.Unproven);

        return SkillsMaterializer.Inspect(here.Path) is (SkillFileProbe.Present, { } moved)
               && row.Receipts.Any(r => r.Matches(moved))
            ? new SkillRelocation(here.Place(row), SkillRelocationOutcome.Moved)
            : new SkillRelocation(row, SkillRelocationOutcome.Gone);
    }
}
