namespace Capacitor.Cli.Core.Skills;

/// <summary>One path this repository is giving up without deleting, and the ledgers that still own
/// it. The survivors take this row's receipts: two owners of one path can hold different ones,
/// because whichever wrote last is the one the bytes match, and which owner leaves first is
/// arbitrary.</summary>
public sealed record LegacyHandoff(OwnedSkillRow Row, IReadOnlyList<string> Survivors);

/// <summary>What migration may do to one repository's legacy ledger. <paramref name="Actionable"/>
/// is false when nothing can be proven — the ledger is there but will not parse, or a sibling that
/// could be hiding the only other owner will not — in which case the files stay and the next sync
/// retries.</summary>
public sealed record LegacyMigrationPlan(
    string LedgerPath, IReadOnlyList<OwnedSkillRow> Delete, IReadOnlyList<LegacyHandoff> Relinquish,
    bool Actionable = true);

/// <summary>The outcome of one retirement: paths it could not remove, and paths whose bytes no
/// receipt it holds accounts for.</summary>
public sealed record LegacyRetirement(IReadOnlyList<string> Refused, IReadOnlyList<string> Unvouched) {
    public static readonly LegacyRetirement Nothing = new([], []);

    public bool Incomplete => Refused.Count > 0 || Unvouched.Count > 0;
}

/// <summary>Retires the user-global copies one repository owns. A global path carries no repository
/// identity, so two repositories can own the same directory — a project-homed skill does exactly
/// that — and deleting one repository's copy would take the other's with it.</summary>
public static class SkillsLegacyMigration {
    /// <summary>The user-global ledger for one (repo, target). Built here for every caller: the
    /// sibling scan below excludes a repository from its own candidate list by comparing this path,
    /// and a repository that failed to recognise its own ledger would read itself as another owner
    /// of everything it owns, stopping migration with nothing failing.</summary>
    public static string ManifestPathFor(string configRoot, string repoHash, string targetKey) =>
        Path.Combine(configRoot, "skills", repoHash, targetKey, "manifest.json");

    public static LegacyMigrationPlan Plan(
            string configRoot, string repoHash, string targetKey, SkillsIdentity current) {
        var mine = ManifestPathFor(configRoot, repoHash, targetKey);
        var read = SkillsLedgerFile.Read(mine, SkillOrigin.Legacy, out var ledger);

        if (read == SkillsLedgerRead.Missing) return new LegacyMigrationPlan(mine, [], []);
        if (read != SkillsLedgerRead.Loaded) return new LegacyMigrationPlan(mine, [], [], Actionable: false);

        var retired = ledger!.Identity;

        List<Sibling> siblings = [];
        foreach (var candidate in Candidates(configRoot, mine)) {
            // One that exists but will not parse could be hiding the only other owner of any owned
            // path; nothing can be proven safe to delete until it is readable or gone.
            if (SkillsLedgerFile.Read(candidate, SkillOrigin.Legacy, out var sibling) != SkillsLedgerRead.Loaded)
                return new LegacyMigrationPlan(mine, [], [], Actionable: false);

            siblings.Add(Sibling.Of(candidate, sibling!));
        }

        List<OwnedSkillRow> delete     = [];
        List<LegacyHandoff> relinquish = [];

        foreach (var row in OwnedSkillRows.Adopt(ledger, (_, _) => { }).Live.Where(Claims)) {
            List<Sibling> others = [.. siblings.Where(s => s.Owns(row.Path))];
            // A remaining owner under the same retired identity is not serving it either.
            var live = others.Any(s => s.Identity is null
                                       || Equals(s.Identity, current)
                                       || !Equals(s.Identity, retired));

            if (others.Count == 0 || !live) delete.Add(row);
            else relinquish.Add(new LegacyHandoff(row, [.. others.Select(s => s.Path)]));
        }

        return new LegacyMigrationPlan(mine, delete, relinquish);
    }

    /// <summary>Carries out one retirement; the caller holds the machine-wide migration lock. The
    /// survivors' merged evidence is saved before the leaver's rows are removed, so a crash between
    /// the two saves leaves a state the next run resolves without loss.</summary>
    public static LegacyRetirement Retire(
            string configRoot, string repoHash, SkillsTarget target, SkillsIdentity current,
            SkillDeletionCause cause, SkillsIdentity? retired) {
        var plan = Plan(configRoot, repoHash, target.Key, current);
        if (!plan.Actionable || (plan.Delete.Count == 0 && plan.Relinquish.Count == 0))
            return LegacyRetirement.Nothing;

        if (SkillsLedgerFile.Read(plan.LedgerPath, SkillOrigin.Legacy, out var ledger) != SkillsLedgerRead.Loaded)
            return LegacyRetirement.Nothing;

        var rows      = OwnedSkillRows.Adopt(ledger!, (_, _) => { });
        var authority = new SkillAuthority(target.LegacyRoot, target.LegacyRoot);

        foreach (var handoff in plan.Relinquish) {
            foreach (var survivor in handoff.Survivors) HandOver(survivor, handoff.Row);

            rows.Remove(handoff.Row.Path);
        }

        foreach (var row in plan.Delete)
            rows.Put(row with {
                State = OwnedSkillState.Owed, Cause = cause,
                IdentityRetired = cause == SkillDeletionCause.Retired ? retired ?? current : null,
            });

        Persist(plan.LedgerPath, ledger!, rows);

        List<string> refused   = [];
        List<string> unvouched = [];

        foreach (var row in rows.Live.Where(r => r.State == OwnedSkillState.Owed).ToList()) {
            var result = SkillsDeletion.Delete(row, authority);

            if (result != SkillDeletionResult.Refused) { SkillsOwnership.Discharge(rows, row, result); continue; }

            // A merged row that matches none of the receipts it holds has had its evidence handed
            // over and still cannot account for the bytes: never deleted, and reported.
            if (row.Inherited is { Length: > 0 }) {
                unvouched.Add(row.Path);
                rows.Put(row with {
                    State = OwnedSkillState.Unverified, Confirmed = null, Inherited = null,
                    Prepared = null, Cause = null, IdentityRetired = null,
                });
            } else {
                refused.Add(row.Path);
            }
        }

        Persist(plan.LedgerPath, ledger!, rows);

        return new LegacyRetirement(refused, unvouched);
    }

    /// <summary>Whether a legacy ledger still holds work a later run owes. A settled or unverified
    /// row is neither: one holds no claim and the other can never be deleted, so counting them
    /// would buy a round trip at every session start that never clears.</summary>
    public static bool HoldsOutstandingWork(SkillsLedger? ledger) => (ledger?.Rows ?? []).Any(Claims);

    static bool Claims(OwnedSkillRow row) =>
        row.State is OwnedSkillState.Published or OwnedSkillState.Owed or OwnedSkillState.Reserved
        && SkillsLedgerValidation.IsLegal(row);

    /// <summary>Whether a sibling ledger still serves a path. A settled row does not: it records a
    /// directory that is not its owner's to remove.</summary>
    static bool Serves(OwnedSkillRow row) => row.State != OwnedSkillState.Settled;

    static void HandOver(string survivorPath, OwnedSkillRow leaving) {
        if (SkillsLedgerFile.Read(survivorPath, SkillOrigin.Legacy, out var survivor) != SkillsLedgerRead.Loaded)
            return;

        var rows    = OwnedSkillRows.Adopt(survivor, (_, _) => { });
        var holding = rows.At(leaving.Path);
        if (holding?.Confirmed is null) return;

        var known    = holding.Receipts.Select(r => r.FileHash).ToHashSet(StringComparer.Ordinal);
        var incoming = leaving.Receipts.Where(r => known.Add(r.FileHash)).ToArray();
        if (incoming.Length == 0) return;

        rows.Put(holding with { Inherited = [.. holding.Inherited ?? [], .. incoming] });
        Persist(survivorPath, survivor!, rows);
    }

    static void Persist(string path, SkillsLedger ledger, OwnedSkillRows rows) {
        var remaining = rows.All;

        if (remaining.Length == 0) {
            try {
                File.Delete(path);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // Every path it owned is gone, so the next sync plans the same empty retirement and
                // retries the removal.
            }
            return;
        }

        SkillsLedgerFile.Save(path, ledger with { Owned = remaining });
    }

    /// <summary>Every other ledger under the config root, compared canonically so a difference of
    /// spelling cannot hide this repository's own from the exclusion.</summary>
    static List<string> Candidates(string configRoot, string minePath) {
        var skills = Path.Combine(configRoot, "skills");
        if (!Directory.Exists(skills)) return [];
        var mine = CanonicalPath.Resolve(minePath);
        return [.. Directory.EnumerateFiles(skills, "manifest.json", SearchOption.AllDirectories)
            .Where(f => !PathComparison.Equal(CanonicalPath.Resolve(f), mine))];
    }

    /// <summary>One sibling ledger beside the set of paths it owns, resolved once. Two ledgers reach
    /// one physical directory through casing, a symlink or a normalization alias, so the recorded
    /// strings are resolved before they are compared: a co-owner missed here has its copy deleted.
    /// The resolution walks the filesystem, so it happens per ledger rather than per ledger per
    /// owned path — this whole scan runs under the machine-wide migration lock.</summary>
    readonly record struct Sibling(string Path, SkillsIdentity? Identity, HashSet<string> Owned) {
        public static Sibling Of(string path, SkillsLedger ledger) =>
            new(path, ledger.Identity,
                ledger.Rows.Where(Serves).Select(r => CanonicalPath.Resolve(r.Path))
                    .ToHashSet(PathComparison.Comparer));

        public bool Owns(string path) => Owned.Contains(CanonicalPath.Resolve(path));
    }
}
