using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Gemini;
using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Skills;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Commands;

/// <summary>
/// <c>kcap skills sync</c> — materializes the server's versioned skill-doc snapshot for this repo
/// into the checkout the sync runs in. Server-canonical and centrally revocable: files land under a
/// kcap namespace inside the checkout's own harness trees, a per-(worktree, target) manifest in the
/// worktree's own git directory records every path kcap owns and the credential it was fetched
/// under, and pruning walks the manifest — never a skills root — so user-authored skills are
/// untouchable.
/// </summary>
class SkillsCommand(
        ConfigRoot config, HarnessRegistry harnesses, IRepositoriesApi repositories,
        GitProviderRouter router, WorkingDirectory workdir, TokenStore tokens,
        ProfileContext profiles, MachineAuth machine, LegacySkillsRoots legacy, TimeProvider time) {
    // The background refresh keys off each manifest's synced_at, so a burst of session starts
    // costs one network round-trip per interval per target, not one per session.
    static readonly TimeSpan AutoSyncInterval = TimeSpan.FromHours(6);

    const int Failed = 1;

    /// <summary>A target left unfinished because a lock it needed was held elsewhere — distinct from
    /// <see cref="Failed"/> so the next start retries the work instead of assuming the holder did it.
    /// A run reporting both reads as incomplete, since a retry is owed either way.</summary>
    const int Incomplete = 2;

    /// <summary>The credential a sync with no stored token and no machine credential records. A server
    /// whose auth discovery reports that none is required answers an unauthenticated request, so
    /// identity cannot refuse on a missing token; the fetch decides, and a later sign-in reads as an
    /// identity change that retires this catalogue.</summary>
    const string AnonymousAccount = "anonymous";

    /// <summary>The harness trees skills materialize into, relative to a session's anchor. A null
    /// vendor is a SHARED tree (several harnesses read it): its snapshot is fetched vendor-less, so
    /// unknown-excludes keeps vendor-restricted docs out of it — those reach their harness through a
    /// vendored tree instead.</summary>
    internal static IReadOnlyList<SkillsTarget> Targets(LegacySkillsRoots legacy) => [
        new("agents", AgentsPaths.RepoSkillsRelativePath, null,
            [HarnessId.Codex, HarnessId.Copilot, HarnessId.Cursor, HarnessId.OpenCode, HarnessId.Pi,
             HarnessId.Antigravity],
            [HarnessId.Codex, HarnessId.Copilot, HarnessId.Cursor, HarnessId.OpenCode, HarnessId.Pi,
             HarnessId.Antigravity]) { LegacyRoot = legacy.Agents },
        new("claude", ClaudePaths.RepoSkillsRelativePath, "claude",
            [HarnessId.Claude],
            [HarnessId.Claude, HarnessId.Copilot, HarnessId.Cursor, HarnessId.OpenCode])
            { LegacyRoot = legacy.Claude },
        new("kiro", KiroPaths.RepoSkillsRelativePath, "kiro",
            [HarnessId.Kiro], [HarnessId.Kiro]) { LegacyRoot = legacy.Kiro },
        // No session has confirmed a repository-local .gemini/skills; the tree is kept on the
        // vendor's documentation, which is why it has a consumer and no reader. Antigravity is not
        // that consumer: it was measured reading the shared tree above, so listing it here would
        // send an Antigravity-only machine to a directory nothing reads.
        new("gemini", GeminiPaths.RepoSkillsRelativePath, null,
            [HarnessId.Gemini], []) { LegacyRoot = legacy.Gemini },
    ];

    /// <summary>The checkout or linked worktree the session is in. Every write below is relative to
    /// it, and it is a parameter rather than ambient state so a startup adapter can pass a session's
    /// own launch directory.</summary>
    internal static string? ResolveAnchor(string cwd) => GitRepository.FindRoot(cwd);

    public async Task<int> HandleSync(bool dryRun, bool auto = false) {
        var cwd = workdir.Path;

        var anchor = ResolveAnchor(cwd);
        if (anchor is null) {
            await Console.Error.WriteLineAsync("Not inside a git repository — run `kcap skills sync` from a repo.");
            return Failed;
        }
        // The git directory resolves through components that need not exist, so a pointer left
        // behind by a removed worktree answers with a path rather than failing. Existence is a
        // separate question, and a missing one is unresolvable — never something to create.
        var gitDir = GitRepository.ResolveGitDir(cwd);
        if (gitDir is null || !Directory.Exists(gitDir)) {
            await Console.Error.WriteLineAsync($"Could not resolve this repository's git directory from {cwd}.");
            return Failed;
        }
        var repo = await RepositoryDetection.DetectRepositoryAsync(router, config, cwd, time);
        if (repo?.Owner is null || repo.RepoName is null) {
            await Console.Error.WriteLineAsync("Could not determine the repo's owner/name from its git remote.");
            return Failed;
        }
        if (profiles.Resolution.ServerUrl is not { Length: > 0 } serverUrl) {
            await Console.Error.WriteLineAsync("No server URL is configured — run `kcap setup`.");
            return Failed;
        }
        var (identity, problem) = await ResolveIdentityAsync(serverUrl);
        if (identity is null) {
            await Console.Error.WriteLineAsync(problem ?? "Not authenticated. Run 'kcap login' to authenticate.");
            return Failed;
        }

        var hash     = RepoHashHelper.ComputeRepoHash(repo.Owner, repo.RepoName);
        var repoHome = $"repo:{repo.Owner}/{repo.RepoName}";

        var adopted = Targets(legacy)
            .Where(t => Adopted(harnesses, t, File.Exists(ManifestPath(gitDir, t.Key)),
                                File.Exists(LegacyManifestPath(hash, t.Key))))
            .ToList();
        if (adopted.Count == 0) return 0;

        // Ahead of the first file and of every lock the targets take: the block names kcap's
        // directories for every target and depends on nothing a fetch returns, while a run whose
        // tail never reached it would leave those directories visible to Git under a fresh refresh
        // stamp that suppresses the retry. A run that cannot write it writes no files either.
        if (!dryRun) {
            var excluded = await ExcludeAsync(anchor, gitDir, hash, auto);
            if (excluded != 0) return excluded;
        }

        var exitCode = 0;
        foreach (var target in adopted)
            exitCode = Math.Max(exitCode, await SyncTargetAsync(
                target, anchor, gitDir, hash, repoHome, identity, dryRun, auto));
        return exitCode;
    }

    /// <summary>Rewrites the shared exclusion block under the repository lock, which is taken alone:
    /// it is the one lock this command never nests with another.</summary>
    async Task<int> ExcludeAsync(string anchor, string gitDir, string hash, bool auto) {
        var repository = TryAcquire(SkillsLocks.Repository(hash));
        if (repository is null) return await ContendedAsync("this repository's exclusion block", null, auto);

        using (repository) {
            try {
                SkillsExclusion.Apply(CommonGitDir(anchor, gitDir), RepoRoot(anchor),
                                      [.. Targets(legacy).Select(t => t.Root(anchor))]);
                return 0;
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                await Console.Error.WriteLineAsync(
                    $"Cannot keep kcap's skills directories out of Git under {anchor}: {ex.Message}");
                return Failed;
            }
        }
    }

    /// <summary>One target's sync. The attempt owns the per-worktree manifest lock for its whole life
    /// and has released it by the time it returns, because the lock the tail needs is shared and
    /// ordered outside it. <c>Settled</c> is false for an outcome that never reached a saved manifest,
    /// which is the one case the tail must not run on.
    ///
    /// <para>The machine-wide migration lock is needed only by a retirement, and whether this run is
    /// one is certain only once the manifest has been read under the inner lock — from where the
    /// outer lock can no longer be taken in order. So the lock-free read decides the first attempt,
    /// and an attempt that finds otherwise hands both locks back rather than holding a shared lock
    /// while it waits for a lock a peer legitimately holds across its fetch. One retry, so it
    /// terminates.</para></summary>
    async Task<int> SyncTargetAsync(
            SkillsTarget target, string anchor, string gitDir, string hash, string repoHome,
            SkillsIdentity identity, bool dryRun, bool auto) {
        var retiring = Retiring(LoadQuietly(ManifestPath(gitDir, target.Key)),
                                LoadQuietly(LegacyManifestPath(hash, target.Key)), identity);

        var attempt = await AttemptTargetAsync(
            target, anchor, gitDir, hash, repoHome, identity, dryRun, auto, takeMigration: retiring);
        if (attempt.NeedsMigration)
            attempt = await AttemptTargetAsync(
                target, anchor, gitDir, hash, repoHome, identity, dryRun, auto, takeMigration: true);

        return attempt.Settled
            ? Math.Max(attempt.Code, await FinishTargetAsync(hash, target, anchor, identity, auto))
            : attempt.Code;
    }

    /// <summary>One attempt at one target, under the locks <paramref name="takeMigration"/> selects.
    /// <c>NeedsMigration</c> means the locked read contradicted the lock-free peek that chose them,
    /// so the caller owes one retry with the migration lock held.</summary>
    internal async Task<(int Code, bool Settled, bool NeedsMigration)> AttemptTargetAsync(
            SkillsTarget target, string anchor, string gitDir, string hash, string repoHome,
            SkillsIdentity identity, bool dryRun, bool auto, bool takeMigration) {
        void Info(string line) { if (!auto) Console.WriteLine(line); }

        var manifestPath = ManifestPath(gitDir, target.Key);
        var legacyPath   = LegacyManifestPath(hash, target.Key);
        var root         = target.Root(anchor);
        var reported     = new HashSet<string>(StringComparer.Ordinal);

        // One line per refused deletion per run: an intent refused before the fetch is retried after
        // it, and refusing twice says nothing the first line did not.
        async Task ReportStuckAsync(IEnumerable<PendingPrune> stuck) {
            foreach (var recorded in stuck)
                if (reported.Add(recorded.Path))
                    await Console.Error.WriteLineAsync(
                        $"Could not remove {recorded.Path}: it does not resolve to a kcap directory "
                      + $"under {recorded.Root}; it stays recorded for the next sync.");
        }

        // What an auto run may skip: a periodic refresh is exactly what a lock holder is already
        // doing. Classified from a read taken without the lock, so it is a hint about how long to
        // wait and never a decision to write. It canonicalizes an anchor, so it reads the
        // filesystem: a refusal there ends this target rather than escaping a run whose streams
        // nobody reads.
        bool owed;
        try {
            owed = auto && Owed(LoadQuietly(manifestPath), identity, anchor,
                                legacyOwnership: LoadQuietly(legacyPath) is not null);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            await Console.Error.WriteLineAsync(Unwritable(target, anchor, ex));
            return (Failed, false, false);
        }

        // The only nesting: migration outside the per-worktree manifest lock, never the other way
        // round, and never with the repository lock, which the run took and released on its own
        // before any target started. Migration is released before the fetch: a shared lock must
        // never span a network request.
        var migrationLock = takeMigration ? TryAcquire(SkillsLocks.Migration) : null;
        if (takeMigration && migrationLock is null)
            return (await ContendedAsync("retiring global skills", target.Key, auto), false, false);

        // One sync per (worktree, target) at a time, machine-wide: a burst of session starts must
        // collapse to ONE refresh — the throttle alone cannot do that, since every child of the
        // burst reads the same stale synced_at before the winner stamps it. The manifest is read
        // UNDER this lock, so waiters see the winner's stamp.
        var manifestLock = TryAcquire(SkillsLocks.Manifest(gitDir, target.Key),
                                      auto && !owed ? TimeSpan.FromMilliseconds(1) : null);
        if (manifestLock is null) {
            migrationLock?.Dispose();
            if (auto) return (owed ? Incomplete : 0, false, false);
            await Console.Error.WriteLineAsync(
                $"Another kcap skills sync is already running for this repo ({target.Key}).");
            return (Failed, false, false);
        }
        using var heldManifestLock = manifestLock;

        SkillsManifest?    manifest   = null;
        List<PendingPrune> journal    = [];
        DateTimeOffset?    lastSynced = null;
        var                failed     = false;
        string[]           anchors    = [anchor];

        // Destinations at THIS anchor that the ledger about to be emptied still vouches for: a
        // checkout moved with its files carries them along, so they exist before the run starts
        // and no surviving row names them.
        List<string> carried = [];
        try {
            if (!TryLoadManifest(manifestPath, out manifest)) return (Failed, false, false);

            // The migration lock is what owns this file; read under it whenever this attempt holds
            // it. Without it the answer only decides whether to ask for a retry, never a write.
            var legacy = LoadQuietly(legacyPath);

            if (Retiring(manifest, legacy, identity) && migrationLock is null)
                return (0, false, true);

            if (auto && AutoThrottled(manifest, time.GetUtcNow())
                     && !Owed(manifest, identity, anchor, legacyOwnership: legacy is not null))
                return (0, false, false);

            journal    = [.. manifest?.PendingPrunes ?? []];
            lastSynced = manifest?.SyncedAt;
            anchors    = TrustedAnchors(manifest, anchor);

            // Two ledgers, two decisions. A legacy retirement that cannot finish — a sibling it
            // cannot read, a vendor root that moved — stays due on every start, and folding the
            // two together would then delete and re-materialize the local catalogue every session
            // and leave nothing behind whenever the replacement fetch failed.
            var localRetiring  = Superseded(manifest, identity);
            var legacyRetiring = Superseded(legacy, identity);

            // Ahead of the fetch and unconditional: a replacement that fails must leave nothing of
            // the previous account behind, locally or in the global trees. The local ledger is then
            // saved owning nothing but the deletions that were refused — dropping those rows would
            // leave their directories with nothing able to prune them — and with no synced_at, so a
            // failed replacement cannot be read as a completed refresh.
            if (localRetiring || legacyRetiring) {
                if (!dryRun) {
                    List<PendingPrune> refusedPrunes = [];
                    if (localRetiring) {
                        var owning = OldRoot(manifest, target, anchor);
                        foreach (var entry in manifest?.Skills ?? []) {
                            var recorded = new PendingPrune(entry.Path, owning);
                            if (!PruneRecorded(recorded, target, anchors)) refusedPrunes.Add(recorded);
                        }
                        foreach (var outstanding in journal)
                            if (!PruneRecorded(outstanding, target, anchors)) refusedPrunes.Add(outstanding);
                    }

                    if (legacyRetiring)
                        foreach (var copy in RetireLegacy(hash, target, identity)) {
                            failed = true;
                            await Console.Error.WriteLineAsync(
                                $"Could not remove the global copy {copy}; it stays recorded for the next sync.");
                        }

                    if (localRetiring) {
                        journal = refusedPrunes;
                        if (refusedPrunes.Count > 0) {
                            failed = true;
                            await ReportStuckAsync(refusedPrunes);
                        }
                        SaveManifest(manifestPath,
                                     BuildManifest(null, [], anchor, identity, target, repoHome,
                                                   syncedAt: null, pending: false, journal, anchors));
                    }
                } else {
                    // The deletion is the most destructive thing this command does and it happens
                    // before the fetch, so a preview that listed only the writes would show none
                    // of it.
                    Info($"[{target.Key}] the recorded account ({RetiredAccount(manifest, legacy, identity)}) "
                       + $"is no longer {identity.Account}; its catalogue goes before a replacement is fetched:");
                    if (localRetiring) {
                        foreach (var entry in manifest?.Skills ?? []) Info($"{"would retire",-12} {entry.Path}");
                        foreach (var outstanding in journal) Info($"{"would retire",-12} {outstanding.Path}");
                        journal.Clear();
                    }
                    if (legacyRetiring)
                        foreach (var copy in PlanLegacy(hash, target, identity)?.Delete ?? [])
                            Info($"{"would retire",-12} {copy}");
                }
                if (localRetiring) {
                    manifest   = null;
                    lastSynced = null;
                }
            }

            if (!localRetiring && manifest is not null && Moved(manifest, anchor)) {
                // Neither the conditional request nor the planner compares destinations, so a
                // manifest recorded at another anchor is discarded as a cache and kept as a ledger:
                // every document is rewritten at the new paths, and every old path is queued for
                // deletion beside the root that authorises deleting it. Nothing has been applied at
                // the new anchor yet, so the previous stamp does not carry over either.
                var owning = OldRoot(manifest, target, anchor);
                journal    = [.. SkillsJournal.Merge(journal, (manifest.Skills ?? [])
                    .Select(entry => new PendingPrune(entry.Path, owning)))];
                // Only the anchor changed, so each entry's destination at the new root is
                // derivable from the slug it owned — and a checkout that moved with its files
                // already has it on disk.
                carried    = [.. (manifest.Skills ?? [])
                    .Select(entry => SkillsMaterializer.SkillDirFor(root, entry.Slug))];
                manifest   = manifest with { Skills = [], Etag = null };
                lastSynced = null;
            }
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            await Console.Error.WriteLineAsync(Unwritable(target, anchor, ex));
            return (Failed, false, false);
        } finally {
            migrationLock?.Dispose();
        }

        // Metadata alone cannot prove a skill is served: a deleted or hand-edited SKILL.md must be
        // re-materialized, so local drift forfeits the conditional request — a 304 would otherwise
        // report "up to date" over a missing file forever. An interrupted publication forfeits it
        // for the same reason: its entries name files that may never have been written.
        var drifted = (manifest?.Skills ?? []).Where(SkillsMaterializer.HasDrifted)
            .Select(e => e.DocId).ToHashSet();
        var conditional = drifted.Count == 0 && manifest?.Pending != true ? manifest?.Etag : null;

        SkillsSnapshotResult fetched;
        try {
            fetched = await repositories.GetSkillsSnapshotAsync(hash, target.Vendor, conditional);
        } catch (CapacitorApiException ex) {
            await Console.Error.WriteLineAsync(ex.Message);
            return (Failed, false, false);
        }

        // A 304 is only ever answered to a conditional request, and only a manifest supplies one.
        if (fetched is SkillsSnapshotResult.NotModified && manifest is not null) {
            Info($"[{target.Key}] skills up to date ({manifest.Skills?.Length ?? 0} materialized).");
            if (dryRun) return (0, false, false);
            try {
                // Unchanged metadata still has to clear an interrupted publication and carry out any
                // deletion still owed, so it settles like any other outcome.
                var stale = Settle(manifestPath, manifest with {
                    Anchor = anchor, Identity = identity, Exposure = Exposure(target),
                }, target, anchors, completed: time.GetUtcNow());
                await ReportStuckAsync(stale);
                return (failed || stale.Length > 0 ? Failed : 0, true, false);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                await Console.Error.WriteLineAsync(Unwritable(target, anchor, ex));
                return (Failed, false, false);
            }
        }
        if (fetched is SkillsSnapshotResult.NotFound) {
            await Console.Error.WriteLineAsync(
                "Repo not found or not visible for this profile. Check `kcap whoami` / your active profile.");
            return (Failed, false, false);
        }
        if (fetched is not SkillsSnapshotResult.Found found) {
            await Console.Error.WriteLineAsync(
                $"Server reported this repo's skills unchanged ({target.Key}) with nothing recorded to serve.");
            return (Failed, false, false);
        }

        var dto      = found.Snapshot;
        var snapshot = dto.Skills ?? [];
        // Whole-snapshot validation BEFORE any filesystem mutation: acting on a partially-valid
        // snapshot and recording its etag would prune real skills, write no replacements, and
        // 304 forever after — refusing outright leaves everything intact and retried in full.
        var unsafeSlugs = snapshot.Where(s => !SkillsSyncPlanner.IsSafeSlug(s.Slug)).ToList();
        if (unsafeSlugs.Count > 0) {
            foreach (var u in unsafeSlugs)
                await Console.Error.WriteLineAsync($"Refusing snapshot: unsafe slug '{u.Slug}'.");
            return (Failed, false, false);
        }
        var plan   = SkillsSyncPlanner.Plan(manifest, snapshot);
        var writes = plan.Writes.Concat(plan.Unchanged.Where(u => drifted.Contains(u.DocId))).ToList();

        // Reconciled against every destination the snapshot occupies, not only the rewritten ones:
        // acting on an intent whose path is live again deletes a served skill and then saves a
        // manifest claiming it exists. Both halves canonicalize paths, so both touch the
        // filesystem.
        PendingPrune[] merged;
        PendingPrune[] owedPrunes;
        try {
            merged     = SkillsJournal.Merge(journal, plan.Prunes.Select(e => new PendingPrune(e.Path, root)));
            owedPrunes = SkillsJournal.Reconcile(
                merged, snapshot.Select(s => SkillsMaterializer.SkillDirFor(root, s.Slug)));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            await Console.Error.WriteLineAsync(Unwritable(target, anchor, ex));
            return (Failed, false, false);
        }

        foreach (var w in writes)
            Info($"{(dryRun ? "would write" : "write"),-12} {SkillsMaterializer.SkillDirFor(root, w.Slug)} (v{w.Version})");
        foreach (var p in owedPrunes)
            Info($"{(dryRun ? "would prune" : "prune"),-12} {p.Path}");
        if (dryRun) return (0, false, false);

        List<SkillSnapshotItem> refused = [];
        List<SkillSnapshotItem> unowned = [];
        PendingPrune[]          stuck;
        SkillSnapshotItem[]     published;
        try {
            // A destination that already exists and no ledger row names is the repository's own,
            // and may be tracked. Recording it would make a later prune delete it whole, so it is
            // refused before ownership is recorded — the journal counts as a ledger row, since a
            // path awaiting deletion is one kcap wrote and a rename can come back to it, and so do
            // the destinations an anchor change carried over.
            var ledger = (manifest?.Skills ?? []).Select(e => e.Path)
                .Concat(merged.Select(p => p.Path)).Concat(carried)
                .Select(CanonicalPath.Resolve).ToHashSet(PathComparison.Comparer);
            foreach (var w in writes) {
                var dir = SkillsMaterializer.SkillDirFor(root, w.Slug);
                if (!Directory.Exists(dir) || ledger.Contains(CanonicalPath.Resolve(dir))) continue;
                unowned.Add(w);
                await Console.Error.WriteLineAsync(
                    $"Refused to write {dir}: the directory already exists and kcap does not own it.");
            }
            var unownedIds = unowned.Select(w => w.DocId).ToHashSet();
            var claimable  = unownedIds.Count == 0
                ? snapshot
                : snapshot.Where(s => !unownedIds.Contains(s.DocId)).ToArray();

            // Ownership before the write, in both directions: a crash between here and the final
            // save leaves every planned path and every owed deletion recorded, so a later sync can
            // finish whichever half was interrupted.
            if (writes.Count > 0 || owedPrunes.Length > 0)
                SaveManifest(manifestPath, BuildManifest(dto.Etag, claimable, anchor, identity, target,
                                                         repoHome, lastSynced, pending: true, owedPrunes, anchors));

            foreach (var w in writes)
                if (!unownedIds.Contains(w.DocId) && !SkillsMaterializer.Write(root, anchor, w)) refused.Add(w);
            foreach (var w in refused)
                await Console.Error.WriteLineAsync(
                    $"Refused to write {SkillsMaterializer.SkillDirFor(root, w.Slug)}: it does not resolve inside {anchor}.");

            // A path that was not published is not owned, and the etag goes with it: a 304 answered
            // to a recorded etag would report "up to date" over a document that never landed. A run
            // that published only part of the snapshot earns no refresh stamp either.
            var refusedIds = refused.Select(w => w.DocId).Concat(unownedIds).ToHashSet();
            published = refusedIds.Count == 0
                ? snapshot
                : snapshot.Where(s => !refusedIds.Contains(s.DocId)).ToArray();
            stuck = Settle(
                manifestPath,
                BuildManifest(refusedIds.Count == 0 ? dto.Etag : null, published, anchor, identity,
                              target, repoHome, lastSynced, pending: false, owedPrunes, anchors),
                target,
                anchors,
                completed: refusedIds.Count == 0 ? time.GetUtcNow() : lastSynced);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            await Console.Error.WriteLineAsync(Unwritable(target, anchor, ex));
            return (Failed, false, false);
        }
        await ReportStuckAsync(stuck);

        Info(writes.Count == 0 && owedPrunes.Length == 0
            ? $"[{target.Key}] skills up to date ({published.Length} materialized)."
            : $"[{target.Key}] synced {writes.Count - refused.Count - unowned.Count} skill(s), "
            + $"pruned {owedPrunes.Length - stuck.Length}; {published.Length} materialized.");

        return (failed || refused.Count + unowned.Count + stuck.Length > 0 ? Failed : 0, true, false);
    }

    /// <summary>The work that has to outlive the manifest lock: retiring the global copies under the
    /// machine-wide migration lock, which is shared and so is never taken while the manifest lock is
    /// held — which is also why it runs after the local manifest has been saved, the order migration
    /// requires.</summary>
    async Task<int> FinishTargetAsync(string hash, SkillsTarget target, string anchor,
                                      SkillsIdentity identity, bool auto) {
        var migration = TryAcquire(SkillsLocks.Migration);
        if (migration is null) return await ContendedAsync("retiring global skills", target.Key, auto);

        var result = 0;
        using (migration) {
            try {
                foreach (var copy in RetireLegacy(hash, target, identity)) {
                    result = Failed;
                    await Console.Error.WriteLineAsync(
                        $"Could not remove the global copy {copy}; it stays recorded for the next sync.");
                }
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                await Console.Error.WriteLineAsync(Unwritable(target, anchor, ex));
                result = Failed;
            }
        }
        return result;
    }

    static async Task<int> ContendedAsync(string work, string? targetKey, bool auto) {
        if (!auto)
            await Console.Error.WriteLineAsync(targetKey is null
                ? $"Another kcap holds the lock for {work}; nothing was synced."
                : $"Another kcap holds the lock for {work} ({targetKey}); this target was left unfinished.");
        return Incomplete;
    }

    /// <summary>A destination the filesystem refused. Reported from the real operation rather than
    /// from a writability probe, which races and can answer differently from the write it predicts.
    /// </summary>
    static string Unwritable(SkillsTarget target, string anchor, Exception ex) =>
        $"Cannot materialize {target.Key} skills under {anchor}: {ex.Message}";

    /// <summary>Whether a target should be synced: a consuming harness is present, or kcap already
    /// owns it — real detection, not destination-parent existence, so a fresh machine with (say)
    /// Codex installed still adopts the shared tree. A target already owned keeps reconciling
    /// (revocation must reach it) even after every consumer is removed.</summary>
    internal static bool Adopted(IHarnessDetection harnesses, SkillsTarget target,
                                 bool hasManifest, bool hasLegacyManifest) =>
        hasManifest || hasLegacyManifest || target.Consumers.Any(harnesses.Detected);

    internal static bool AutoThrottled(SkillsManifest? manifest, DateTimeOffset now) =>
        // A future stamp (clock correction, tampered file) must read as stale, not as an
        // unbounded suppression of every revocation refresh until that future arrives.
        manifest?.SyncedAt is { } syncedAt && now - syncedAt is { } age
            && age >= TimeSpan.Zero && age < AutoSyncInterval;

    /// <summary>Work the refresh throttle must not suppress: an interrupted publication, a deletion
    /// still owed, a retired credential, a moved anchor, or a readable legacy ledger still standing.
    /// The throttle exists to collapse a burst of periodic refreshes, and none of these is one — a
    /// refresh stamp is written before the tail that retires the global copies, so the local ledger
    /// on its own cannot tell a finished migration from an interrupted one. Readable, because a
    /// ledger that will not parse is cleanup nothing can perform: counting it would spend a
    /// round-trip per session start that never clears.</summary>
    internal static bool Owed(SkillsManifest? manifest, SkillsIdentity identity, string anchor,
                              bool legacyOwnership) =>
        legacyOwnership
        || (manifest is not null
            && (manifest.Pending
                || manifest.PendingPrunes is { Length: > 0 }
                || Superseded(manifest, identity)
                || Moved(manifest, anchor)));

    /// <summary>Whether either ledger records a credential that is not the current one. The legacy
    /// one counts because a target can be adopted on it alone, and a retirement nothing sees is one
    /// nothing carries out before the fetch — leaving the previous account's global copies
    /// loadable.</summary>
    static bool Retiring(SkillsManifest? local, SkillsManifest? legacy, SkillsIdentity identity) =>
        Superseded(local, identity) || Superseded(legacy, identity);

    /// <summary>A ledger recording no identity is adopted rather than retired: its files are still
    /// this profile's, and nothing contradicts them.</summary>
    static bool Superseded(SkillsManifest? manifest, SkillsIdentity identity) =>
        manifest?.Identity is not null && !Equals(manifest.Identity, identity);

    /// <summary>The account a retirement is leaving, for the line that reports it.</summary>
    static string RetiredAccount(SkillsManifest? local, SkillsManifest? legacy, SkillsIdentity identity) =>
        (Superseded(local, identity) ? local : legacy)!.Identity!.Account;

    static bool Moved(SkillsManifest manifest, string anchor) =>
        manifest.Anchor is not null
        && !PathComparison.Equal(CanonicalPath.Resolve(manifest.Anchor), CanonicalPath.Resolve(anchor));

    /// <summary>The credential a snapshot is fetched under, and the problem to report when there is
    /// none to name. Read from what is already on disk and never refreshed: the subject claim belongs
    /// to the account rather than to a token's freshness, and refreshing would spend a single-use
    /// rotating credential — and a network round-trip — on a value that is already stored.</summary>
    async Task<(SkillsIdentity? Identity, string? Problem)> ResolveIdentityAsync(string serverUrl) {
        // Normalized, so a configured trailing slash does not read as a different server and retire
        // a catalogue nobody changed.
        var server = AppConfig.NormalizeUrl(serverUrl);

        // A runner has no token store at all. Its client id is the account it authenticates as and
        // costs nothing to read, where minting the bearer it would otherwise be read from is a call.
        // Half a credential is a state `kcap login` cannot repair and a runner cannot perform, so the
        // credential's own account of it is what reaches the operator.
        if (machine.Intended)
            return machine.TryRead(out var problem) is { } credential
                ? (new SkillsIdentity($"machine:{credential.ClientId}", server), null)
                : (null, problem);

        var stored = await tokens.LoadForProfileAsync(profiles.Name);
        return stored?.AccessToken is { Length: > 0 } accessToken
               && JwtClaims.TryGetString(accessToken, "sub") is { Length: > 0 } account
            ? (new SkillsIdentity(account, server), null)
            : (new SkillsIdentity(AnonymousAccount, server), null);
    }

    /// <summary>Per worktree by construction, and removed with the worktree: Git owns this
    /// directory.</summary>
    static string ManifestPath(string gitDir, string targetKey) =>
        Path.Combine(gitDir, "kcap", "skills", targetKey + ".json");

    string LegacyManifestPath(string hash, string targetKey) =>
        SkillsLegacyMigration.ManifestPathFor(config.Directory, hash, targetKey);

    /// <summary>The working tree the anchor sits in, which the exclusion patterns are written
    /// relative to: <c>info/exclude</c> is resolved from the shared common directory but its
    /// anchored patterns apply at the top level of whichever working tree reads them, so a linked
    /// worktree's block is relative to its own root — not to the main checkout's, which its path
    /// need not sit under at all.</summary>
    static string RepoRoot(string anchor) => GitRepository.FindRoot(anchor) ?? anchor;

    /// <summary>The git directory <c>info/exclude</c> resolves to: a linked worktree shares the main
    /// checkout's, so a single file covers the repository and all its worktrees. Both halves are
    /// canonicalized because <see cref="GitRepository.ResolveGitDir"/> resolves links and
    /// <see cref="GitRepository.ResolveMainRepoRoot"/> does not, and under a symlinked path prefix
    /// the two otherwise disagree in shape. A submodule, whose <c>.git</c> is a file rather than a
    /// directory, keeps its own.</summary>
    static string CommonGitDir(string anchor, string gitDir) {
        var root = GitRepository.ResolveMainRepoRoot(RepoRoot(anchor));
        var main = CanonicalPath.Resolve(Path.Combine(root, ".git"));
        return Directory.Exists(main) ? main : gitDir;
    }

    static string OldRoot(SkillsManifest? manifest, SkillsTarget target, string anchor) =>
        target.Root(manifest?.Anchor ?? anchor);

    /// <summary>The anchors a deletion may be aimed at: this run's own, the one the ledger was last
    /// written at, and the ones it recorded occupying while rows were outstanding. All three come
    /// from what kcap itself wrote when that anchor was live; nothing a row asserts about itself is
    /// ever admitted, which is the whole point of the rule.</summary>
    static string[] TrustedAnchors(SkillsManifest? manifest, string anchor) {
        List<string> trusted = [anchor];
        var          seen    = new HashSet<string>(PathComparison.Comparer) { anchor };

        foreach (var candidate in (string[])[manifest?.Anchor ?? "", .. manifest?.PruneAnchors ?? []])
            if (candidate.Length > 0 && seen.Add(candidate)) trusted.Add(candidate);

        return [.. trusted];
    }

    /// <summary>The anchors a ledger has to keep recording: every trusted one some surviving row is
    /// rooted at. An anchor no row references is dropped, so the history cannot grow without
    /// bound.</summary>
    static string[] ReferencedAnchors(IReadOnlyList<string> trusted, SkillsTarget target,
                                      IReadOnlyList<PendingPrune> journal) {
        if (journal.Count == 0) return [];
        var roots = journal.Select(p => CanonicalPath.Resolve(p.Root)).ToHashSet(PathComparison.Comparer);
        return [.. trusted.Where(a => roots.Contains(CanonicalPath.Resolve(target.Root(a))))];
    }

    /// <summary>Deletes one recorded directory, authorised by trusted state rather than by the
    /// record: the root beside the path only selects which trusted anchor answers for it, and the
    /// deletion itself is held to a direct kcap-owned child of that anchor's own target tree. A root
    /// matching none of them is refused and survives, so a later run retries it; a directory already
    /// gone is the intent satisfied.</summary>
    static bool PruneRecorded(PendingPrune recorded, SkillsTarget target, IReadOnlyList<string> anchors) {
        if (!Directory.Exists(Path.GetFullPath(recorded.Path))) return true;

        foreach (var anchor in anchors) {
            var root = target.Root(anchor);
            if (!PathComparison.Equal(CanonicalPath.Resolve(root), CanonicalPath.Resolve(recorded.Root)))
                continue;
            return SkillsMaterializer.Prune(root, anchor, recorded.Path);
        }

        return false;
    }

    /// <summary>Retires the user-global copies this repository owns, returning the ones it could not
    /// remove; the caller holds the migration lock. A path that is kept — or whose deletion was
    /// refused — stays recorded, because a directory no manifest owns is one nothing can ever prune.
    /// A path another live ledger owns is not kept: a copy every owner goes on claiming is one
    /// nobody is ever last out of.</summary>
    IReadOnlyList<string> RetireLegacy(string hash, SkillsTarget target, SkillsIdentity identity) {
        if (PlanLegacy(hash, target, identity) is not { } plan) return [];

        List<string> refused = [];
        List<string> keep    = [.. plan.Keep];
        foreach (var path in plan.Delete)
            if (!PruneGlobal(target, path)) {
                refused.Add(path);
                keep.Add(path);
            }

        if (keep.Count == 0) {
            try {
                File.Delete(plan.ManifestPath);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // Every path it owned is gone, so the next sync plans the same empty deletion and
                // retries the removal.
            }
            return refused;
        }
        var retained = keep.ToHashSet(PathComparison.Comparer);
        if (LoadQuietly(plan.ManifestPath) is { Skills: not null } kept)
            SaveManifest(plan.ManifestPath,
                         kept with { Skills = [.. kept.Skills.Where(e => retained.Contains(e.Path))] });
        return refused;
    }

    /// <summary>What a retirement would delete for this target, or null when it must not act: no
    /// ledger, a ledger that will not parse — the only record of directories nothing else can name,
    /// so deleting it would orphan them — or a plan that decided nothing while still keeping
    /// entries. Changes nothing on disk, and the existence check comes first because planning scans
    /// every global manifest under the config root: the common case, nothing left to retire, must
    /// not pay for that on each sync.</summary>
    LegacyMigrationPlan? PlanLegacy(string hash, SkillsTarget target, SkillsIdentity identity) {
        if (!File.Exists(LegacyManifestPath(hash, target.Key))) return null;

        var plan = SkillsLegacyMigration.Plan(config.Directory, hash, target.Key, identity);
        if (plan.Unreadable) return null;
        return plan.Delete.Count == 0 && plan.Keep.Count > 0 ? null : plan;
    }

    /// <summary>Deletes one user-global directory: a direct kcap-owned child of the tree this target
    /// occupied, which is the only place a global copy was ever written. A ledger naming anywhere
    /// else — a hand edit, or a vendor root that has since moved — is refused and stays recorded. A
    /// directory already gone counts as removed.</summary>
    static bool PruneGlobal(SkillsTarget target, string path) =>
        !Directory.Exists(Path.GetFullPath(path))
        || SkillsMaterializer.Prune(target.LegacyRoot, target.LegacyRoot, path);

    /// <summary>Carries out the deletions a manifest still owes and clears its pending flag, keeping
    /// an intent that was refused so a later run retries it. The ledger is saved after the
    /// deletions, so an interruption leaves a path owned rather than orphaned, and only a settlement
    /// that left nothing undone earns <paramref name="completed"/> as its refresh stamp — a fresh one
    /// otherwise would let the throttle read an incomplete sync as a completed one.</summary>
    static PendingPrune[] Settle(string manifestPath, SkillsManifest manifest, SkillsTarget target,
                                 IReadOnlyList<string> anchors, DateTimeOffset? completed) {
        List<PendingPrune> stuck = [];
        foreach (var recorded in manifest.PendingPrunes ?? [])
            if (!PruneRecorded(recorded, target, anchors)) stuck.Add(recorded);
        SaveManifest(manifestPath, manifest with {
            Pending = false, PendingPrunes = [.. stuck],
            PruneAnchors = ReferencedAnchors(anchors, target, stuck),
            SyncedAt = stuck.Count == 0 ? completed : manifest.SyncedAt,
        });
        return [.. stuck];
    }

    static SkillsManifest BuildManifest(
            string? etag, IReadOnlyList<SkillSnapshotItem> snapshot, string anchor,
            SkillsIdentity identity, SkillsTarget target, string repoHome, DateTimeOffset? syncedAt,
            bool pending, IReadOnlyList<PendingPrune> journal, IReadOnlyList<string> anchors) {
        var root = target.Root(anchor);
        return new() {
            Etag     = etag, SyncedAt = syncedAt, Anchor = anchor, Identity = identity,
            Exposure = Exposure(target), Pending = pending, PendingPrunes = [.. journal],
            PruneAnchors = ReferencedAnchors(anchors, target, journal),
            Skills   = [.. snapshot.Select(s => new SkillsManifestEntry {
                DocId = s.DocId, Slug = s.Slug, Version = s.Version, ContentHash = s.ContentHash,
                Path = SkillsMaterializer.SkillDirFor(root, s.Slug),
                FileHash = SkillsMaterializer.FileHash(SkillsSyncPlanner.RenderSkillFile(s)),
                // A server that sends no home is answering for the repository that asked.
                Home = s.Home ?? repoHome, Applicability = s.Applicability,
            })],
        };
    }

    /// <summary>Every harness measured to read the tree this target writes to — recorded because
    /// placement cannot enforce a vendor restriction for a tree several harnesses read.</summary>
    static string[] Exposure(SkillsTarget target) => [.. target.Readers.Select(r => r.VendorId)];

    IDisposable? TryAcquire(string name, TimeSpan? timeout = null) {
        try {
            return config.AcquireLock(name, timeout);
        } catch (TimeoutException) {
            return null;
        }
    }

    // The manifest is the ownership ledger, and the two failure classes diverge: an unreadable
    // file may be transient (sharing violation), so the sync ABORTS rather than reconciling
    // ledger-less over a still-valid file; a corrupt file proceeds from scratch only once the
    // evidence is genuinely preserved aside — a failed preserve also aborts.
    static bool TryLoadManifest(string path, out SkillsManifest? manifest) {
        manifest = null;
        if (!File.Exists(path)) return true;
        string text;
        try {
            text = File.ReadAllText(path);
        } catch (Exception ex) {
            Console.Error.WriteLine($"Cannot read skills manifest ({ex.Message}); aborting sync.");
            return false;
        }
        try {
            manifest = JsonSerializer.Deserialize(text, CapacitorJsonContext.Default.SkillsManifest);
        } catch (JsonException) {
            manifest = null;
        }
        // A parseable `null` or a missing skills collection is no ledger either — under a stored
        // etag it would 304 forever with zero owned paths, stranding every prior directory. Same
        // recovery route as unparseable content.
        if (manifest?.Skills is not null) return true;
        manifest = null;
        try {
            File.Move(path, path + ".corrupt", overwrite: true);
        } catch (Exception ex) {
            Console.Error.WriteLine($"Corrupt skills manifest could not be preserved ({ex.Message}); aborting sync.");
            return false;
        }
        Console.Error.WriteLine($"Warning: corrupt skills manifest moved aside ({path}.corrupt); re-syncing from scratch.");
        return true;
    }

    /// <summary>Reads a manifest, treating an unreadable or unparseable file as absent. A caller that
    /// must not act on a superseded copy holds the lock that owns the file first.</summary>
    static SkillsManifest? LoadQuietly(string path) {
        try {
            // A plain read denies Write and Delete to every other handle, and on Windows that
            // sharing is mandatory — this file has a concurrent writer by design, and its atomic
            // replace renames over the destination, so both must be shared or the publication
            // fails inside this read's window.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                                              FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize(stream, CapacitorJsonContext.Default.SkillsManifest);
        } catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) {
            return null;
        }
    }

    static void SaveManifest(string path, SkillsManifest manifest) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.Replace(path, JsonSerializer.Serialize(manifest, CapacitorJsonContext.Default.SkillsManifest));
    }
}
