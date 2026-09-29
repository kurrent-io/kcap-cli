using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Commands;

/// <summary>
/// One target's sync, from the ledger on disk to the ledger on disk. The caller owns the locks: the
/// per-worktree ledger lock for the whole of this, the machine-wide migration lock only while
/// <see cref="PrepareAsync"/> runs, and never either across the fetch in
/// <see cref="ReconcileAsync"/>.
///
/// <para>Every mutation goes through the working rows and reaches disk through <c>Save</c>, so a
/// completion and the supersessions it causes are one ledger replacement and a partially updated
/// group is never visible.</para>
/// </summary>
sealed class SkillsSyncRun(
        ConfigRoot config, IRepositoriesApi repositories, TimeProvider time, SkillsTarget target,
        string anchor, string gitDir, string hash, string repoHome, SkillsIdentity identity,
        bool dryRun, bool auto) {
    readonly string _ledgerPath = SkillsCommand.LedgerPath(gitDir, target.Key);
    readonly string _legacyPath = SkillsLegacyMigration.ManifestPathFor(config.Directory, hash, target.Key);
    readonly string _root       = target.Root(anchor);

    /// <summary>Documents this run must neither write nor let a deletion overtake: an operation
    /// whose bytes are somebody else's, a destination the ledger does not own, and a write the
    /// filesystem refused.</summary>
    readonly HashSet<Guid> _heldBack = [];

    /// <summary>One line per path per run. An intent refused before the fetch is retried after it,
    /// and refusing twice says nothing the first line did not.</summary>
    readonly HashSet<string> _reported = new(PathComparison.Comparer);

    SkillsLedger   _ledger = new();
    OwnedSkillRows _rows   = OwnedSkillRows.Adopt(null, (_, _) => { });
    bool           _exists;
    bool           _failed;
    bool           _dirty;
    int            _stuck;

    /// <summary>Whether this target owes work the refresh throttle must not suppress, read without
    /// the lock: a hint about how long to wait, never a decision to write.</summary>
    public bool OutstandingFromPeek() =>
        SkillsCommand.Outstanding(
            SkillsLedgerFile.ReadQuietly(_ledgerPath, SkillOrigin.Repository),
            SkillsLegacyMigration.HoldsOutstandingWork(
                SkillsLedgerFile.ReadQuietly(_legacyPath, SkillOrigin.Legacy)),
            identity, anchor);

    /// <summary>Everything that happens before a byte leaves the machine: read the ledger, resolve
    /// every operation left in flight, settle a checkout that moved, and carry out a retirement.
    /// A non-null answer ends the target here.</summary>
    public async Task<(int Code, bool Settled, bool NeedsMigration)?> PrepareAsync(bool migrationHeld) {
        if (await LoadAsync() is { } refusal) return refusal;

        // The migration lock is what owns this file; read under it whenever this attempt holds it.
        // Without it the answer only decides whether to ask for a retry, never a write. The status
        // is kept because a ledger that cannot be read is not evidence of anything.
        var legacyRead = SkillsLedgerFile.Read(_legacyPath, SkillOrigin.Legacy, out var legacy);

        // The envelope's credential is what the catalogue belongs to; an operation's is a fallback
        // for a ledger interrupted before the envelope recorded one. A retained operation under
        // another account is protected by its own refusal and must not retire rows this account has
        // since published.
        var recorded = SkillsCommand.Recorded(_ledger.Identity, _rows.Live);

        // Two ledgers, two decisions. A legacy retirement that cannot finish stays due on every
        // start, and folding the two together would delete and re-materialize the local catalogue
        // every session and leave nothing behind whenever the replacement fetch failed.
        var retiringLocal = Superseded(recorded);
        // A global ledger written before identities were recorded carries none, and one that will
        // not parse says nothing at all. Neither is evidence that its copies are this account's.
        var retiringLegacy = Superseded(legacy?.Identity)
                          || _ledger.LegacyRetirement is not null
                          || (retiringLocal && legacyRead != SkillsLedgerRead.Missing
                              && legacy?.Identity is null);

        if ((retiringLocal || retiringLegacy) && !migrationHeld) return (0, false, true);

        if (auto && SkillsCommand.AutoThrottled(_ledger, time.GetUtcNow())
                 && !SkillsCommand.Outstanding(_exists ? _ledger : null,
                                               SkillsLegacyMigration.HoldsOutstandingWork(legacy),
                                               identity, anchor))
            return (0, false, false);

        await ResolveOperationsAsync();
        await RelocateAsync();

        if (retiringLocal || retiringLegacy) {
            await RetireAsync(retiringLocal, retiringLegacy, legacy, recorded);

            return null;
        }

        if (_dirty) Save();

        // A deletion already owed is a decision already committed, and the run that owed it wrote
        // whatever replaces it. Carrying it out does not wait on a fetch that may never succeed.
        if (!dryRun && _rows.Live.Any(r => r.State == OwnedSkillState.Owed))
            await DischargeAsync(reservedFor: null);

        return null;
    }

    /// <summary>The fetch and everything it decides. No shared lock is held here.</summary>
    public async Task<(int Code, bool Settled, bool NeedsMigration)> ReconcileAsync() {
        SkillsSnapshotResult fetched;
        try {
            fetched = await repositories.GetSkillsSnapshotAsync(hash, target.Vendor, Eligible() ? _ledger.Etag : null);
        } catch (CapacitorApiException ex) {
            await Console.Error.WriteLineAsync(ex.Message);
            return (SkillsCommand.Failed, false, false);
        }

        if (fetched is SkillsSnapshotResult.NotModified && _exists) return await UnchangedAsync();
        if (fetched is SkillsSnapshotResult.NotFound) {
            await Console.Error.WriteLineAsync(
                "Repo not found or not visible for this profile. Check `kcap whoami` / your active profile.");
            return (SkillsCommand.Failed, false, false);
        }
        if (fetched is not SkillsSnapshotResult.Found found) {
            await Console.Error.WriteLineAsync(
                $"Server reported this repo's skills unchanged ({target.Key}) with nothing recorded to serve.");
            return (SkillsCommand.Failed, false, false);
        }

        var snapshot = found.Snapshot.Skills ?? [];
        // Whole-snapshot validation BEFORE any filesystem mutation: acting on a partially-valid
        // snapshot and recording its etag would delete real skills, write no replacements, and
        // 304 forever after.
        var unsafeSlugs = snapshot.Where(s => !SkillsRendering.IsSafeSlug(s.Slug)).ToList();
        if (unsafeSlugs.Count > 0) {
            foreach (var item in unsafeSlugs)
                await Console.Error.WriteLineAsync($"Refusing snapshot: unsafe slug '{item.Slug}'.");
            return (SkillsCommand.Failed, false, false);
        }

        var plan = SkillsReconciler.Plan(_rows, snapshot, target, anchor, _heldBack);
        foreach (var refused in plan.Refusals) {
            _failed = true;
            _heldBack.Add(refused.Item.DocId);
            await Console.Error.WriteLineAsync($"Refused to write {refused.Destination}: {refused.Reason}.");
        }

        foreach (var write in plan.Writes)
            Info($"{(dryRun ? "would write" : "write"),-12} {write.At.Path} (v{write.Item.Version})");
        foreach (var path in Doomed(snapshot))
            Info($"{(dryRun ? "would prune" : "prune"),-12} {path}");
        if (dryRun) return (0, false, false);

        var published = await PublishAsync(plan, found.Snapshot.Etag, snapshot);

        Info(plan.Writes.Count == 0 && _stuck == 0
            ? $"[{target.Key}] skills up to date ({Materialized()} materialized)."
            : $"[{target.Key}] synced {published} skill(s); {Materialized()} materialized.");

        // Settled is what lets the tail retire the global copies, and a target that could not
        // publish has not migrated: a stuck deletion is owed work, a refused write is a failure to
        // have written at all.
        return (_failed ? SkillsCommand.Failed : 0, _heldBack.Count == 0, false);
    }

    async Task<int> PublishAsync(SkillsSyncPlan plan, string? etag, IReadOnlyList<SkillSnapshotItem> snapshot) {
        List<(PlannedSkillWrite Write, byte[] Content, SkillReceipt Receipt)> pending = [];
        foreach (var write in plan.Writes) {
            // The receipt is taken over the same bytes the write publishes, never over the text
            // they encode.
            var content = SkillsMaterializer.Encode(SkillsRendering.RenderSkillFile(write.Item));

            pending.Add((write, content, new SkillReceipt {
                FileHash = SkillsMaterializer.FileHash(content),
                Document = SkillDocument.Of(write.Item, repoHome),
            }));
        }

        // The credential is recorded before any byte is written: an operation left in flight has to
        // be attributable to an account, or the next run cannot tell a transition from a first sync.
        _ledger = _ledger with { Identity = identity, Exposure = SkillsCommand.Exposure(target) };

        // Ownership before the write: a crash between here and the next save leaves every planned
        // destination recorded with the hash it was about to receive, and nothing else.
        foreach (var (write, _, receipt) in pending) SkillsOwnership.Prepare(_rows, write.At, receipt, identity);
        if (pending.Count > 0) Save();

        List<OwnedSkillRow> attempted = [];
        foreach (var (write, content, _) in pending) {
            if (SkillsMaterializer.Write(write.At.Path, anchor, content)) {
                if (_rows.At(write.At.Path) is { } row) attempted.Add(row);
                continue;
            }

            _failed = true;
            _heldBack.Add(write.Item.DocId);
            await Console.Error.WriteLineAsync(
                $"Refused to write {write.At.Path}: it does not resolve inside {anchor}.");
            // A refused write leaves the row exactly as it was; only the attempt is abandoned.
            if (_rows.At(write.At.Path) is { } refused) _rows.Put(SkillsRecovery.Abandon(refused));
        }

        var landed = await CompleteAsync(attempted);

        SkillsOwnership.Revoke(_rows, snapshot.Select(s => s.DocId).ToHashSet());

        // A path that was not published is not owned, and the etag goes with it: a 304 answered to a
        // recorded etag would report "up to date" over a document that never landed.
        _ledger = _ledger with { Etag = _heldBack.Count == 0 ? etag : null };
        Save();

        await DischargeAsync(Destinations(snapshot));

        Stamp();

        return landed;
    }

    /// <summary>Reads back what was just written and decides it the same way a later run would,
    /// then commits each completion with the supersessions it causes.</summary>
    async Task<int> CompleteAsync(IReadOnlyList<OwnedSkillRow> attempted) {
        var landed = 0;

        foreach (var row in attempted) {
            var at       = Destination(row.Prepared!.Intended.Document.Slug);
            var recovery = SkillsRecovery.Resolve(row, at);

            if (recovery.Outcome == SkillRecoveryOutcome.Refused) {
                _failed = true;
                _heldBack.Add(row.Prepared.Intended.Document.DocId);
                await ReportAsync(row.Path, Foreign(row.Path));
                continue;
            }

            _rows.Relocate(recovery.From, recovery.Row);

            if (recovery.Outcome != SkillRecoveryOutcome.Landed) {
                _failed = true;
                _heldBack.Add(row.Prepared.Intended.Document.DocId);
                await ReportAsync(row.Path, $"Wrote {SkillsMaterializer.SkillFileFor(row.Path)} and could not read it back; it stays recorded for the next sync.");
                continue;
            }

            landed++;
            SkillsOwnership.Complete(_rows, At(recovery.Row), recovery.Row.Confirmed!);
        }

        return landed;
    }

    /// <summary>A no-change answer settles like any other outcome: an operation resolved, a deletion
    /// still owed carried out, the ledger's identity and exposure refreshed.</summary>
    async Task<(int Code, bool Settled, bool NeedsMigration)> UnchangedAsync() {
        Info($"[{target.Key}] skills up to date ({Materialized()} materialized).");
        if (dryRun) return (0, false, false);

        _ledger = _ledger with { Identity = identity, Exposure = SkillsCommand.Exposure(target) };
        Save();

        await DischargeAsync(reservedFor: null);

        Stamp();

        return (_failed ? SkillsCommand.Failed : 0, true, false);
    }

    /// <summary>Only a run that left nothing undone earns a refresh stamp: a fresh one otherwise
    /// would let the throttle read an incomplete sync as a completed one.</summary>
    void Stamp() {
        if (_heldBack.Count == 0 && _stuck == 0) _ledger = _ledger with { SyncedAt = time.GetUtcNow() };
        Save();
    }

    async Task<(int Code, bool Settled, bool NeedsMigration)?> LoadAsync() {
        // The ledger is the ownership record, and the two failure classes diverge: an unreadable
        // file may be transient (sharing violation), so the sync ABORTS rather than reconciling
        // ledger-less over a still-valid file; a corrupt file proceeds from scratch only once the
        // evidence is genuinely preserved aside — a failed preserve also aborts.
        var read = SkillsLedgerFile.Read(_ledgerPath, SkillOrigin.Repository, out var ledger);

        if (read == SkillsLedgerRead.Unreadable) {
            await Console.Error.WriteLineAsync(
                $"Cannot read the skills ownership ledger ({_ledgerPath}); aborting sync.");
            return (SkillsCommand.Failed, false, false);
        }

        if (read == SkillsLedgerRead.Corrupt) {
            try {
                File.Move(_ledgerPath, _ledgerPath + ".corrupt", overwrite: true);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                await Console.Error.WriteLineAsync(
                    $"Corrupt skills ownership ledger could not be preserved ({ex.Message}); aborting sync.");
                return (SkillsCommand.Failed, false, false);
            }
            await Console.Error.WriteLineAsync(
                $"Warning: corrupt skills ownership ledger moved aside ({_ledgerPath}.corrupt); re-syncing from scratch.");
            ledger = null;
        }

        _exists = ledger is not null;
        _ledger = ledger ?? new SkillsLedger();
        _rows   = OwnedSkillRows.Adopt(ledger, RefuseRow);

        return null;
    }

    void RefuseRow(OwnedSkillRow row, string reason) {
        _failed = true;
        Console.Error.WriteLine($"Ignoring the ownership row for {row.Path}: {reason}; nothing acts on it.");
    }

    /// <summary>Decides every operation left in flight before anything else looks at the rows. An
    /// operation authorised under another account is resolved here too, which is why a retirement
    /// never has to replay one: deleting first would compare the old receipt against bytes the old
    /// account itself wrote and refuse its own file.</summary>
    async Task ResolveOperationsAsync() {
        foreach (var row in _rows.Live.Where(r => r.Prepared is not null).ToList()) {
            var document = row.Prepared!.Intended.Document;
            var recovery = SkillsRecovery.Resolve(
                row, row.Origin == SkillOrigin.Repository ? Destination(document.Slug) : null);

            if (recovery.Outcome == SkillRecoveryOutcome.Refused) {
                _failed = true;
                _heldBack.Add(document.DocId);
                await ReportAsync(row.Path, Foreign(row.Path));
                continue;
            }

            _rows.Relocate(recovery.From, recovery.Row);
            _dirty = true;

            if (recovery.Outcome == SkillRecoveryOutcome.Landed && recovery.Row.State == OwnedSkillState.Published)
                SkillsOwnership.Complete(_rows, At(recovery.Row), recovery.Row.Confirmed!);
        }
    }

    /// <summary>Settles a checkout that was renamed or moved with its files. A row keeps its old
    /// tuple until one outcome or the other is verified: re-anchoring in place would discard the old
    /// path's ownership while its copy may still exist.</summary>
    async Task RelocateAsync() {
        foreach (var row in _rows.Live.Where(r => SkillsRelocation.IsCandidate(r, anchor)).ToList()) {
            var moved = SkillsRelocation.Resolve(row, Destination(row.Confirmed!.Document.Slug));

            if (moved.Outcome == SkillRelocationOutcome.Unproven) {
                await ReportAsync(row.Path, $"Keeping {row.Path} owned: it is not established as gone, "
                                          + "so nothing here is treated as the same copy moved.");
                continue;
            }

            if (moved.Outcome != SkillRelocationOutcome.Moved) continue;

            _rows.Relocate(row.Path, moved.Row);
            _dirty = true;
        }
    }

    /// <summary>The deletions an account change ordered. They run before the fetch — a replacement
    /// that fails must leave nothing of the previous account behind — and the obligation to settle
    /// the global copies is recorded before the local catalogue's identity is replaced, because
    /// nothing else would remember whose files they are.</summary>
    async Task RetireAsync(bool local, bool legacy, SkillsLedger? legacyLedger, SkillsIdentity? recorded) {
        var retired = recorded ?? legacyLedger?.Identity ?? _ledger.LegacyRetirement ?? identity;

        if (dryRun) {
            Info($"[{target.Key}] the recorded account ({retired.Account}) is no longer {identity.Account}; "
               + "its catalogue goes before a replacement is fetched:");
            if (local)
                // Reduced to bare destinations, which is what the real deletion leaves behind them,
                // so what follows previews the materialization from scratch rather than the
                // catalogue that is about to go.
                foreach (var row in _rows.Live.Where(r => r.Origin == SkillOrigin.Repository).ToList()) {
                    if (row.HoldsAClaim) Info($"{"would retire",-12} {row.Path}");
                    _rows.Put(row with {
                        State = OwnedSkillState.Reserved, Confirmed = null, Inherited = null,
                        Prepared = null, Cause = null, IdentityRetired = null,
                    });
                }
            if (legacy)
                foreach (var row in SkillsLegacyMigration.Plan(config.Directory, hash, target.Key, identity).Delete)
                    Info($"{"would retire",-12} {row.Path}");
            return;
        }

        var obligation = legacy ? _ledger.LegacyRetirement ?? legacyLedger?.Identity ?? retired : null;

        if (local) {
            SkillsOwnership.Retire(_rows, SkillOrigin.Repository, retired);
            _ledger = _ledger with { LegacyRetirement = obligation ?? _ledger.LegacyRetirement };
            Save();

            await DischargeAsync(reservedFor: null);
        }

        if (legacy && await SkillsCommand.ReportRetirementAsync(SkillsLegacyMigration.Retire(
                config.Directory, hash, target, identity, SkillDeletionCause.Retired, obligation)) != 0)
            _failed = true;

        var discharged = LegacyDischarged();

        if (local) {
            // The local ledger owns nothing but the deletions that were refused, and carries no
            // refresh stamp: a failed replacement must not read as a completed refresh.
            _ledger = _ledger with {
                Identity = identity, Etag = null, SyncedAt = null,
                Exposure = SkillsCommand.Exposure(target),
                LegacyRetirement = discharged ? null : obligation,
            };
            Save();
        } else if (_exists && discharged && _ledger.LegacyRetirement is not null) {
            _ledger = _ledger with { LegacyRetirement = null };
            Save();
        }
    }

    /// <summary>Whether the obligation to settle the global copies can be let go of. Only absence
    /// or a readable ledger with nothing left in it establishes that: a ledger that will not read
    /// is exactly the case the obligation exists to survive.</summary>
    bool LegacyDischarged() {
        var read = SkillsLedgerFile.Read(_legacyPath, SkillOrigin.Legacy, out var ledger);

        return read == SkillsLedgerRead.Missing
            || (read == SkillsLedgerRead.Loaded && !SkillsLegacyMigration.HoldsOutstandingWork(ledger));
    }

    /// <summary>Carries out every deletion owed and releases every reservation whose reason has gone
    /// away, then records the outcome each reached. A row whose document was held back is not
    /// attempted: copy before delete, and a deletion that outran its replacement leaves the document
    /// served from nowhere. A retirement is exempt — it runs before any replacement exists.</summary>
    async Task DischargeAsync(IReadOnlySet<string>? reservedFor) {
        foreach (var row in _rows.Live.ToList()) {
            var releasing = reservedFor is not null && SkillsOwnership.IsReleasable(row, reservedFor);
            if (row.State != OwnedSkillState.Owed && !releasing) continue;
            if (row.Cause != SkillDeletionCause.Retired
                    && row.Document is { } held && _heldBack.Contains(held.DocId)) continue;

            if (SkillAuthority.For(row, target, anchor) is not { } authority) {
                Stick();
                await ReportAsync(row.Path, $"Could not remove {row.Path}: it does not resolve to a kcap "
                                          + $"directory under {row.Root}; it stays recorded for the next sync.");
                continue;
            }

            var result = releasing
                ? SkillsDeletion.Release(row, authority)
                : SkillsDeletion.Delete(row, authority);

            if (result is SkillDeletionResult.Refused or SkillDeletionResult.Unvouched) {
                Stick();
                await ReportAsync(row.Path, $"Could not remove {SkillsMaterializer.SkillFileFor(row.Path)}: "
                                          + (result == SkillDeletionResult.Unvouched
                                              ? "it is not the file kcap recorded writing there"
                                              : "it could not be read or does not resolve to a kcap directory")
                                          + "; it stays recorded for the next sync.");
                continue;
            }

            SkillsOwnership.Discharge(_rows, row, result);
        }

        Save();
    }

    void Stick() {
        _failed = true;
        _stuck++;
    }

    void Save() {
        if (dryRun) return;

        SkillsLedgerFile.Save(_ledgerPath, _ledger with { Owned = _rows.All });
        _exists = true;
        _dirty  = false;
    }

    bool Superseded(SkillsIdentity? recorded) => recorded is not null && !Equals(recorded, identity);

    SkillDestination Destination(string slug) => SkillDestination.For(target, anchor, slug);

    static SkillDestination At(OwnedSkillRow row) => new(row.Path, row.Root, row.Anchor ?? row.Root);

    IReadOnlySet<string> Destinations(IReadOnlyList<SkillSnapshotItem> snapshot) =>
        snapshot.Select(s => PathComparison.Key(CanonicalPath.Resolve(Destination(s.Slug).Path)))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>The paths a run would delete, for a preview that would otherwise show only the
    /// writes.</summary>
    IEnumerable<string> Doomed(IReadOnlyList<SkillSnapshotItem> snapshot) {
        var live = Destinations(snapshot);

        return _rows.Live
            .Where(r => r.Origin == SkillOrigin.Repository
                        && (r.State == OwnedSkillState.Owed
                            || (r.HoldsAClaim
                                && !live.Contains(PathComparison.Key(CanonicalPath.Resolve(r.Path))))))
            .Select(r => r.Path);
    }

    int Materialized() => _rows.Live.Count(r => r is { Origin: SkillOrigin.Repository, State: OwnedSkillState.Published });

    /// <summary>Cache eligibility binds to completed publication: a conditional request is only
    /// eligible when every planned destination for this identity reached its receipt at the path it
    /// named. A settled row is not one — it holds no claim at all.</summary>
    bool Eligible() =>
        _heldBack.Count == 0
        && _ledger.Etag is { Length: > 0 }
        && _ledger.Identity is not null && Equals(_ledger.Identity, identity)
        && _rows.Live.All(row => row.Origin != SkillOrigin.Repository
                                 || row.State == OwnedSkillState.Settled
                                 || Satisfies(row));

    bool Satisfies(OwnedSkillRow row) =>
        row is { State: OwnedSkillState.Published, Prepared: null, Confirmed: { } confirmed }
        && row.Anchor is { } recorded
        && PathComparison.Equal(CanonicalPath.Resolve(recorded), CanonicalPath.Resolve(anchor))
        && PathComparison.Equal(
               CanonicalPath.Resolve(row.Path),
               CanonicalPath.Resolve(SkillsMaterializer.SkillDirFor(_root, confirmed.Document.Slug)))
        && SkillsMaterializer.Inspect(row.Path) is (SkillFileProbe.Present, { } hash)
        && confirmed.Matches(hash);

    static string Foreign(string path) =>
        $"Refusing {SkillsMaterializer.SkillFileFor(path)}: it is neither what kcap was writing there "
      + "nor what it last wrote, so it is neither adopted nor deleted.";

    async Task ReportAsync(string path, string line) {
        if (_reported.Add(path)) await Console.Error.WriteLineAsync(line);
    }

    void Info(string line) {
        if (!auto) Console.WriteLine(line);
    }
}
