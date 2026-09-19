namespace Capacitor.Cli.Core.Skills;

public enum SkillRecoveryOutcome {
    /// <summary>The write landed. The row is completed where the bytes actually are.</summary>
    Landed,

    /// <summary>The write did not land, or nothing is there to judge. The operation is discarded;
    /// an ordinary run reserves and writes again, and a retirement cancels it rather than replaying
    /// it.</summary>
    Discarded,

    /// <summary>Somebody other than us wrote there. The bytes are neither adopted nor deleted, the
    /// operation stays unresolved, and the document is withheld from this run.</summary>
    Refused,
}

/// <summary>One row's resolution: the row as it now stands, the path it was recorded at, and which
/// of the three comparisons answered.</summary>
public sealed record SkillRecovery(OwnedSkillRow Row, string From, SkillRecoveryOutcome Outcome);

/// <summary>
/// Writing a file and saving the ledger cannot be made atomic, so a later run decides from the rows
/// alone whether a write landed — by reading the file and comparing it three ways against the
/// operation's intended hash and the row's confirmed one.
/// </summary>
public static class SkillsRecovery {
    /// <summary>Resolves one prepared operation. <paramref name="travelled"/> is the single
    /// destination the operation itself named under this run's anchor: a match anywhere else proves
    /// nothing and authorises nothing.</summary>
    public static SkillRecovery Resolve(OwnedSkillRow row, SkillDestination? travelled) {
        if (row.Prepared is not { } prepared)
            return new SkillRecovery(row, row.Path, SkillRecoveryOutcome.Discarded);

        var (probe, hash) = SkillsMaterializer.Inspect(row.Path);

        if (probe is SkillFileProbe.Present) {
            if (prepared.Intended.Matches(hash!)) return Complete(row, row.Path, place: null);
            if (row.Confirmed?.Matches(hash!) == true) return Discard(row);

            return new SkillRecovery(row, row.Path, SkillRecoveryOutcome.Refused);
        }

        if (probe is not SkillFileProbe.Absent)
            return new SkillRecovery(row, row.Path, SkillRecoveryOutcome.Refused);

        // A write can land and then travel before it is recorded: the row still names a path whose
        // file is genuinely absent, while the bytes sit under the new anchor. The same two-sided
        // test relocation uses, applied to the operation rather than to the receipt.
        if (travelled is { } moved
                && !PathComparison.Equal(CanonicalPath.Resolve(moved.Path), CanonicalPath.Resolve(row.Path))
                && SkillsMaterializer.Inspect(moved.Path) is (SkillFileProbe.Present, { } there)
                && prepared.Intended.Matches(there))
            return Complete(row, row.Path, moved);

        return Discard(row);
    }

    /// <summary>Promotes the operation to the receipt, at the place its bytes were found. A row
    /// already owed keeps its cause: recording what was written never publishes, and never revives
    /// a row that has been retired.</summary>
    static SkillRecovery Complete(OwnedSkillRow row, string from, SkillDestination? place) {
        var completed = row with {
            Confirmed = row.Prepared!.Intended,
            Prepared  = null,
            State     = row.State == OwnedSkillState.Owed ? OwnedSkillState.Owed : OwnedSkillState.Published,
        };

        return new SkillRecovery(place?.Place(completed) ?? completed, from, SkillRecoveryOutcome.Landed);
    }

    /// <summary>Gives up the attempt and nothing else: a refused write leaves the row's receipt, its
    /// document, its state and its retirement cause exactly as they were. A first write that failed
    /// before the file existed keeps its reservation, so the destination stays ours to retry.
    /// </summary>
    public static OwnedSkillRow Abandon(OwnedSkillRow row) => Discard(row).Row;

    static SkillRecovery Discard(OwnedSkillRow row) {
        var discarded = row with {
            Prepared = null,
            State = row.State switch {
                OwnedSkillState.Owed             => OwnedSkillState.Owed,
                _ when row.Confirmed is not null => OwnedSkillState.Published,
                _                                => OwnedSkillState.Reserved,
            },
        };

        return new SkillRecovery(discarded, row.Path, SkillRecoveryOutcome.Discarded);
    }
}
