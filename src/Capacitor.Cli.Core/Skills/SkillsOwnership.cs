namespace Capacitor.Cli.Core.Skills;

/// <summary>
/// Every transition one run makes over the rows. Each mutates the working set; the caller saves it
/// once, so a completion and the supersessions it causes reach disk as one ledger replacement and a
/// partially updated group is never visible.
/// </summary>
public static class SkillsOwnership {
    /// <summary>Records the write before any byte of it exists, leaving whatever was confirmed —
    /// and whatever deletion is owed — exactly as it was. Requesting a replacement is not
    /// publishing one, and only a completion may discharge the obligation a row already
    /// carries.</summary>
    public static void Prepare(OwnedSkillRows rows, SkillDestination at, SkillReceipt intended,
                               SkillsIdentity identity) {
        var existing = rows.At(at.Path);
        var placed   = existing is null
            ? new OwnedSkillRow {
                  Path = at.Path, Root = at.Root, Anchor = at.Anchor,
                  Origin = SkillOrigin.Repository, State = OwnedSkillState.Reserved,
              }
            : at.Place(existing with {
                  State = existing.State switch {
                      OwnedSkillState.Owed              => OwnedSkillState.Owed,
                      _ when existing.Confirmed is null => OwnedSkillState.Reserved,
                      _                                 => OwnedSkillState.Published,
                  },
              });

        rows.Put(placed with {
            Prepared = new PreparedSkillWrite {
                Operation = Guid.NewGuid(), Intended = intended, Identity = identity,
            },
        });
    }

    /// <summary>Completes one publication and, in the same group, transitions the specific rows it
    /// supersedes.</summary>
    public static void Complete(OwnedSkillRows rows, SkillDestination at, SkillReceipt receipt) {
        var existing = rows.At(at.Path) ?? new OwnedSkillRow {
            Path = at.Path, Root = at.Root, Anchor = at.Anchor,
            Origin = SkillOrigin.Repository, State = OwnedSkillState.Published, Confirmed = receipt,
        };

        rows.Put(at.Place(existing with {
            Confirmed = receipt, Prepared = null, State = OwnedSkillState.Published,
            Cause = null, IdentityRetired = null,
        }));

        foreach (var other in rows.Live.ToList()) {
            if (other.Origin != SkillOrigin.Repository || other.State != OwnedSkillState.Published) continue;
            if (other.Document?.DocId != receipt.Document.DocId) continue;
            if (PathComparison.Equal(CanonicalPath.Resolve(other.Path), CanonicalPath.Resolve(at.Path))) continue;

            rows.Put(other with { State = OwnedSkillState.Owed, Cause = SkillDeletionCause.Superseded });
        }
    }

    /// <summary>Owes a deletion for every published path whose document the snapshot has stopped
    /// serving. A document served somewhere else is not this: that transition belongs to the
    /// publication that replaces it, and a refused replacement must not release the copy it was
    /// meant to replace.</summary>
    public static void Revoke(OwnedSkillRows rows, IReadOnlySet<Guid> served) {
        foreach (var row in rows.Live.ToList()) {
            if (row.Origin != SkillOrigin.Repository || row.State != OwnedSkillState.Published) continue;
            if (row.Document is { } document && served.Contains(document.DocId)) continue;

            rows.Put(row with { State = OwnedSkillState.Owed, Cause = SkillDeletionCause.Revoked });
        }
    }

    /// <summary>Owes a deletion for everything one origin holds, because the account that fetched it
    /// is no longer the current one. The account travels with each row: the replacement catalogue is
    /// saved under the new one, and nothing else would remember whose files these are.</summary>
    public static void Retire(OwnedSkillRows rows, SkillOrigin origin, SkillsIdentity retired) {
        foreach (var row in rows.Live.ToList()) {
            if (row.Origin != origin) continue;
            if (row.State is OwnedSkillState.Settled or OwnedSkillState.Unverified) continue;

            rows.Put(row with {
                State = OwnedSkillState.Owed, Cause = SkillDeletionCause.Retired, IdentityRetired = retired,
            });
        }
    }

    /// <summary>Owes a deletion for the given paths because a repository-local copy now serves
    /// them.</summary>
    public static void Supersede(OwnedSkillRows rows, IEnumerable<OwnedSkillRow> superseded) {
        foreach (var row in superseded)
            rows.Put(row with { State = OwnedSkillState.Owed, Cause = SkillDeletionCause.Superseded });
    }

    /// <summary>Whether a reservation's reason has gone away: its document is no longer served, or
    /// it is published somewhere else instead. Never merely because an attempt failed — a
    /// destination the snapshot still names stays reserved, so the next run retries it rather than
    /// finding a directory the attempt created and refusing it as someone else's.</summary>
    public static bool IsReleasable(OwnedSkillRow row, IReadOnlySet<string> reservedFor) =>
        row is { State: OwnedSkillState.Reserved, Prepared: null }
        && !reservedFor.Contains(PathComparison.Key(CanonicalPath.Resolve(row.Path)));

    /// <summary>What a finished deletion or release leaves behind: nothing at all, or a directory
    /// that is not ours to remove.</summary>
    public static void Discharge(OwnedSkillRows rows, OwnedSkillRow row, SkillDeletionResult result) {
        if (result == SkillDeletionResult.Removed) { rows.Remove(row.Path); return; }
        if (result != SkillDeletionResult.Settled) return;

        rows.Put(row with {
            State = OwnedSkillState.Settled, Confirmed = null, Inherited = null, Prepared = null,
            Cause = null, IdentityRetired = null,
        });
    }
}
