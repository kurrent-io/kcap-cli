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
        ProfileContext profiles, MachineAuth machine, TimeProvider time) {
    // The background refresh keys off each manifest's synced_at, so a burst of session starts
    // costs one network round-trip per interval per target, not one per session.
    static readonly TimeSpan AutoSyncInterval = TimeSpan.FromHours(6);

    const int Failed = 1;

    /// <summary>A target left unfinished because a lock it needed was held elsewhere — distinct from
    /// <see cref="Failed"/> so the next start retries the work instead of assuming the holder did it.
    /// A run reporting both reads as incomplete, since a retry is owed either way.</summary>
    const int Incomplete = 2;

    /// <summary>The harness trees skills materialize into, relative to a session's anchor. A null
    /// vendor is a SHARED tree (several harnesses read it): its snapshot is fetched vendor-less, so
    /// unknown-excludes keeps vendor-restricted docs out of it — those reach their harness through a
    /// vendored tree instead.</summary>
    internal static IReadOnlyList<SkillsTarget> Targets() => [
        new("agents", AgentsPaths.RepoSkillsRelativePath, null,
            [HarnessId.Codex, HarnessId.Copilot, HarnessId.Cursor, HarnessId.OpenCode, HarnessId.Pi,
             HarnessId.Antigravity],
            [HarnessId.Codex, HarnessId.Copilot, HarnessId.Cursor, HarnessId.OpenCode, HarnessId.Pi,
             HarnessId.Antigravity]),
        new("claude", ClaudePaths.RepoSkillsRelativePath, "claude",
            [HarnessId.Claude],
            [HarnessId.Claude, HarnessId.Copilot, HarnessId.Cursor, HarnessId.OpenCode]),
        new("kiro", KiroPaths.RepoSkillsRelativePath, "kiro",
            [HarnessId.Kiro], [HarnessId.Kiro]),
        // No session has confirmed a repository-local .gemini/skills; the tree is kept on the
        // vendor's documentation, which is why it has a consumer and no reader.
        new("gemini", GeminiPaths.RepoSkillsRelativePath, null,
            [HarnessId.Gemini, HarnessId.Antigravity], []),
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
        var identity = await ResolveIdentityAsync(serverUrl);
        if (identity is null) {
            await Console.Error.WriteLineAsync("Not authenticated. Run 'kcap login' to authenticate.");
            return Failed;
        }

        var hash     = RepoHashHelper.ComputeRepoHash(repo.Owner, repo.RepoName);
        var repoHome = $"repo:{repo.Owner}/{repo.RepoName}";

        var exitCode = 0;
        foreach (var target in Targets()) {
            var hasManifest       = File.Exists(ManifestPath(gitDir, target.Key));
            var hasLegacyManifest = File.Exists(LegacyManifestPath(hash, target.Key));
            if (!Adopted(harnesses, target, hasManifest, hasLegacyManifest)) continue;
            exitCode = Math.Max(exitCode, await SyncTargetAsync(
                target, anchor, gitDir, hash, repoHome, identity, dryRun, auto));
        }
        return exitCode;
    }

    /// <summary>One target's sync. The publication owns the per-worktree manifest lock for its whole
    /// life and has released it by the time it returns, because the two locks the tail needs are
    /// shared and ordered outside it. <c>Settled</c> is false for an outcome that never reached a
    /// saved manifest, which is the one case the tail must not run on.</summary>
    async Task<int> SyncTargetAsync(
            SkillsTarget target, string anchor, string gitDir, string hash, string repoHome,
            SkillsIdentity identity, bool dryRun, bool auto) {
        var (code, settled) = await PublishTargetAsync(
            target, anchor, gitDir, hash, repoHome, identity, dryRun, auto);

        return settled
            ? Math.Max(code, await FinishTargetAsync(hash, target, anchor, gitDir, identity, auto))
            : code;
    }

    async Task<(int Code, bool Settled)> PublishTargetAsync(
            SkillsTarget target, string anchor, string gitDir, string hash, string repoHome,
            SkillsIdentity identity, bool dryRun, bool auto) {
        void Info(string line) { if (!auto) Console.WriteLine(line); }

        var manifestPath = ManifestPath(gitDir, target.Key);
        var root         = target.Root(anchor);

        // What an auto run may skip: a periodic refresh is exactly what a lock holder is already
        // doing, while recovery, a retired credential and a moved anchor are work only this run
        // owes. Classified from a read taken without the lock, so it is a hint about how long to
        // wait and never a decision to write.
        var owed = auto && Owed(LoadQuietly(manifestPath), identity, anchor);

        // Lock order is migration, then repository, then the per-worktree manifest, and no path
        // takes them the other way round. Migration is machine-wide, so it is released before the
        // fetch — a shared lock must never span a network request, and the only thing it spans here
        // is a bounded wait for the inner lock. It is taken for every run rather than only for a
        // retirement because whether this is one is knowable only once the manifest has been read
        // under the inner lock, by which point up-ordering is no longer possible.
        var migrationLock = TryAcquire(SkillsLocks.Migration);
        if (migrationLock is null)
            return (await ContendedAsync("retiring global skills", target, auto), false);

        // One sync per (worktree, target) at a time, machine-wide: a burst of session starts must
        // collapse to ONE refresh — the throttle alone cannot do that, since every child of the
        // burst reads the same stale synced_at before the winner stamps it. The manifest is read
        // UNDER this lock, so waiters see the winner's stamp.
        var manifestLock = TryAcquire(SkillsLocks.Manifest(gitDir, target.Key),
                                      auto && !owed ? TimeSpan.FromMilliseconds(1) : null);
        if (manifestLock is null) {
            migrationLock.Dispose();
            if (auto) return (owed ? Incomplete : 0, false);
            await Console.Error.WriteLineAsync(
                $"Another kcap skills sync is already running for this repo ({target.Key}).");
            return (Failed, false);
        }
        using var heldManifestLock = manifestLock;

        SkillsManifest?    manifest = null;
        List<PendingPrune> journal  = [];
        try {
            if (!TryLoadManifest(manifestPath, out manifest)) return (Failed, false);
            if (auto && AutoThrottled(manifest, time.GetUtcNow()) && !Owed(manifest, identity, anchor))
                return (0, false);

            journal = [.. manifest?.PendingPrunes ?? []];

            if (manifest is not null && Retiring(manifest, identity)) {
                // Ahead of the fetch and unconditional: a replacement that fails must leave nothing
                // of the previous account behind, locally or in the global trees. The ledger is then
                // saved owning nothing, so the failure cannot leave it claiming what was just
                // deleted — nor the retired credential it was fetched under.
                if (!dryRun) {
                    var owning = OldRoot(manifest, target, anchor);
                    foreach (var entry in manifest.Skills ?? [])
                        PruneRecorded(new PendingPrune(entry.Path, owning), target);
                    foreach (var outstanding in journal) PruneRecorded(outstanding, target);
                    RetireLegacy(hash, target, identity);
                    SaveManifest(manifestPath, BuildManifest(null, [], anchor, identity, target,
                                                             repoHome, pending: false, []));
                }
                manifest = null;
                journal.Clear();
            } else if (manifest is not null && Moved(manifest, anchor)) {
                // Neither the conditional request nor the planner compares destinations, so a
                // manifest recorded at another anchor is discarded as a cache and kept as a ledger:
                // every document is rewritten at the new paths, and every old path is queued for
                // deletion beside the root that authorises deleting it.
                var owning = OldRoot(manifest, target, anchor);
                journal  = [.. SkillsJournal.Merge(journal, (manifest.Skills ?? [])
                    .Select(entry => new PendingPrune(entry.Path, owning)))];
                manifest = manifest with { Skills = [], Etag = null };
            }
        } finally {
            migrationLock.Dispose();
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
            return (Failed, false);
        }

        // A 304 is only ever answered to a conditional request, and only a manifest supplies one.
        if (fetched is SkillsSnapshotResult.NotModified && manifest is not null) {
            Info($"[{target.Key}] skills up to date ({manifest.Skills?.Length ?? 0} materialized).");
            if (dryRun) return (0, false);
            // Unchanged metadata still has to clear an interrupted publication and carry out any
            // deletion still owed, so it settles like any other outcome.
            var stale = Settle(manifestPath, manifest with {
                SyncedAt = time.GetUtcNow(), Anchor = anchor, Identity = identity,
                Exposure = Exposure(target),
            }, target);
            await ReportStuckAsync(stale);
            return (stale.Length > 0 ? Failed : 0, true);
        }
        if (fetched is SkillsSnapshotResult.NotFound) {
            await Console.Error.WriteLineAsync(
                "Repo not found or not visible for this profile. Check `kcap whoami` / your active profile.");
            return (Failed, false);
        }
        if (fetched is not SkillsSnapshotResult.Found found) {
            await Console.Error.WriteLineAsync(
                $"Server reported this repo's skills unchanged ({target.Key}) with nothing recorded to serve.");
            return (Failed, false);
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
            return (Failed, false);
        }
        var plan   = SkillsSyncPlanner.Plan(manifest, snapshot);
        var writes = plan.Writes.Concat(plan.Unchanged.Where(u => drifted.Contains(u.DocId))).ToList();

        // Reconciled against every destination the snapshot occupies, not only the rewritten ones:
        // acting on an intent whose path is live again deletes a served skill and then saves a
        // manifest claiming it exists.
        var owedPrunes = SkillsJournal.Reconcile(
            SkillsJournal.Merge(journal, plan.Prunes.Select(e => new PendingPrune(e.Path, root))),
            snapshot.Select(s => SkillsMaterializer.SkillDirFor(root, s.Slug)));

        foreach (var w in writes)
            Info($"{(dryRun ? "would write" : "write"),-12} {SkillsMaterializer.SkillDirFor(root, w.Slug)} (v{w.Version})");
        foreach (var p in owedPrunes)
            Info($"{(dryRun ? "would prune" : "prune"),-12} {p.Path}");
        if (dryRun) return (0, false);

        // Ownership before the write, in both directions: a crash between here and the final save
        // leaves every planned path and every owed deletion recorded, so a later sync can finish
        // whichever half was interrupted.
        if (writes.Count > 0 || owedPrunes.Length > 0)
            SaveManifest(manifestPath, BuildManifest(dto.Etag, snapshot, anchor, identity, target,
                                                     repoHome, pending: true, owedPrunes));

        var refused = new List<SkillSnapshotItem>();
        foreach (var w in writes)
            if (!SkillsMaterializer.Write(root, anchor, w)) refused.Add(w);
        foreach (var w in refused)
            await Console.Error.WriteLineAsync(
                $"Refused to write {SkillsMaterializer.SkillDirFor(root, w.Slug)}: it does not resolve inside {anchor}.");

        // A path that was not published is not owned, and the etag goes with it: a 304 answered to a
        // recorded etag would report "up to date" over a document that never landed.
        var refusedIds = refused.Select(w => w.DocId).ToHashSet();
        var published  = refusedIds.Count == 0
            ? snapshot
            : snapshot.Where(s => !refusedIds.Contains(s.DocId)).ToArray();
        var stuck = Settle(
            manifestPath,
            BuildManifest(refusedIds.Count == 0 ? dto.Etag : null, published, anchor, identity, target,
                          repoHome, pending: false, owedPrunes),
            target);
        await ReportStuckAsync(stuck);

        Info(writes.Count == 0 && owedPrunes.Length == 0
            ? $"[{target.Key}] skills up to date ({published.Length} materialized)."
            : $"[{target.Key}] synced {writes.Count - refused.Count} skill(s), "
            + $"pruned {owedPrunes.Length - stuck.Length}; {published.Length} materialized.");

        return (refused.Count + stuck.Length > 0 ? Failed : 0, true);
    }

    /// <summary>The work that has to outlive the manifest lock: retiring the global copies under the
    /// machine-wide migration lock, then rewriting the shared exclusion block under the repository
    /// lock. Both are shared, so neither is ever taken while the manifest lock is held — which is
    /// also why they run after the local manifest has been saved, the order migration requires.
    /// </summary>
    async Task<int> FinishTargetAsync(string hash, SkillsTarget target, string anchor, string gitDir,
                                      SkillsIdentity identity, bool auto) {
        var result = 0;

        var migration = TryAcquire(SkillsLocks.Migration);
        if (migration is null) {
            result = await ContendedAsync("retiring global skills", target, auto);
        } else {
            using (migration) RetireLegacy(hash, target, identity);
        }

        var repository = TryAcquire(SkillsLocks.Repository(hash));
        if (repository is null) {
            return Math.Max(result, await ContendedAsync("this repository's exclusion block", target, auto));
        }
        using (repository)
            SkillsExclusion.Apply(CommonGitDir(anchor, gitDir), anchor,
                                  [.. Targets().Select(t => t.Root(anchor))]);
        return result;
    }

    static async Task<int> ContendedAsync(string work, SkillsTarget target, bool auto) {
        if (!auto)
            await Console.Error.WriteLineAsync(
                $"Another kcap holds the lock for {work} ({target.Key}); this target was left unfinished.");
        return Incomplete;
    }

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
    /// still owed, a retired credential or a moved anchor. The throttle exists to collapse a burst of
    /// periodic refreshes, and none of these is one.</summary>
    internal static bool Owed(SkillsManifest? manifest, SkillsIdentity identity, string anchor) =>
        manifest is not null
        && (manifest.Pending
            || manifest.PendingPrunes is { Length: > 0 }
            || Retiring(manifest, identity)
            || Moved(manifest, anchor));

    /// <summary>The recorded credential is not the current one. A manifest recording no identity is
    /// adopted rather than retired: its files are still this profile's, and nothing contradicts
    /// them.</summary>
    static bool Retiring(SkillsManifest manifest, SkillsIdentity identity) =>
        manifest.Identity is not null && !Equals(manifest.Identity, identity);

    static bool Moved(SkillsManifest manifest, string anchor) =>
        manifest.Anchor is not null
        && !string.Equals(CanonicalPath.Resolve(manifest.Anchor), CanonicalPath.Resolve(anchor),
                          StringComparison.Ordinal);

    /// <summary>The credential a snapshot is fetched under. Read from what is already on disk and
    /// never refreshed: the subject claim belongs to the account rather than to a token's freshness,
    /// and refreshing would spend a single-use rotating credential — and a network round-trip — on a
    /// value that is already stored.</summary>
    async Task<SkillsIdentity?> ResolveIdentityAsync(string serverUrl) {
        // Normalized, so a configured trailing slash does not read as a different server and retire
        // a catalogue nobody changed.
        var server = AppConfig.NormalizeUrl(serverUrl);

        // A runner has no token store at all. Its client id is the account it authenticates as and
        // costs nothing to read, where minting the bearer it would otherwise be read from is a call.
        if (machine.Intended)
            return machine.TryRead(out _) is { } credential
                ? new SkillsIdentity($"machine:{credential.ClientId}", server)
                : null;

        var stored = await tokens.LoadForProfileAsync(profiles.Name);
        return stored?.AccessToken is { Length: > 0 } accessToken
               && JwtClaims.TryGetString(accessToken, "sub") is { Length: > 0 } account
            ? new SkillsIdentity(account, server)
            : null;
    }

    /// <summary>Per worktree by construction, and removed with the worktree: Git owns this
    /// directory.</summary>
    static string ManifestPath(string gitDir, string targetKey) =>
        Path.Combine(gitDir, "kcap", "skills", targetKey + ".json");

    string LegacyManifestPath(string hash, string targetKey) =>
        config.Path("skills", hash, targetKey, "manifest.json");

    /// <summary>The git directory <c>info/exclude</c> resolves to: a linked worktree shares the main
    /// checkout's, so one block covers the repository and all its worktrees. Both halves are
    /// canonicalized because <see cref="GitRepository.ResolveGitDir"/> resolves links and
    /// <see cref="GitRepository.ResolveMainRepoRoot"/> does not, and under a symlinked path prefix
    /// the two otherwise disagree in shape. A submodule, whose <c>.git</c> is a file rather than a
    /// directory, keeps its own.</summary>
    static string CommonGitDir(string anchor, string gitDir) {
        var main = CanonicalPath.Resolve(Path.Combine(GitRepository.ResolveMainRepoRoot(anchor), ".git"));
        return Directory.Exists(main) ? main : gitDir;
    }

    static string OldRoot(SkillsManifest manifest, SkillsTarget target, string anchor) =>
        target.Root(manifest.Anchor ?? anchor);

    /// <summary>Deletes one recorded directory, authorised by the root recorded beside it. The anchor
    /// is recovered by stripping the target's tree back off that root, because the current anchor
    /// cannot contain a path left behind at another one, and a root that does not end in this
    /// target's tree is not a root this target ever wrote. A directory already gone is the intent
    /// satisfied; any other refusal has to survive so a later run retries it.</summary>
    static bool PruneRecorded(PendingPrune recorded, SkillsTarget target) {
        var root = Path.GetFullPath(recorded.Root);
        var tree = Path.DirectorySeparatorChar + target.RelativePath;
        if (root.Length <= tree.Length || !root.EndsWith(tree, StringComparison.Ordinal)) return false;
        return !Directory.Exists(Path.GetFullPath(recorded.Path))
               || SkillsMaterializer.Prune(root, root[..^tree.Length], recorded.Path);
    }

    /// <summary>Retires the user-global copies this repository owns; the caller holds the migration
    /// lock. An empty deletion list with entries still kept is either the planner refusing to decide
    /// or a catalogue another repository owns outright, and neither is safe to drop. A kept path
    /// stays recorded, so a later sync can retire it once that owner is gone.</summary>
    void RetireLegacy(string hash, SkillsTarget target, SkillsIdentity identity) {
        // Planning scans every global manifest under the config root, so the common case — nothing
        // left to retire — must not pay for it on each sync.
        if (!File.Exists(LegacyManifestPath(hash, target.Key))) return;

        var plan = SkillsLegacyMigration.Plan(config.Directory, hash, target.Key, identity);
        if (plan.Delete.Count == 0 && plan.Keep.Count > 0) return;

        foreach (var path in plan.Delete) PruneGlobal(path);

        if (plan.Keep.Count == 0) {
            try {
                File.Delete(plan.ManifestPath);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // The files are gone either way; the next sync retries the manifest's removal.
            }
            return;
        }
        if (LoadQuietly(plan.ManifestPath) is { Skills: not null } kept)
            SaveManifest(plan.ManifestPath,
                         kept with { Skills = [.. kept.Skills.Where(e => plan.Keep.Contains(e.Path))] });
    }

    /// <summary>Deletes one user-global directory. Its authorising root is the parent recorded in the
    /// ledger — an absolute path written under a home that may since have moved — so the
    /// <c>kcap-</c> leaf rule, not containment, is what holds the deletion to a directory kcap
    /// created.</summary>
    static void PruneGlobal(string path) {
        var full = Path.GetFullPath(path);
        if (Path.GetDirectoryName(full) is { } parent) SkillsMaterializer.Prune(parent, parent, path);
    }

    /// <summary>Carries out the deletions a manifest still owes and clears its pending flag, keeping
    /// an intent that was refused so a later run retries it. The ledger is saved after the
    /// deletions, so an interruption leaves a path owned rather than orphaned.</summary>
    static PendingPrune[] Settle(string manifestPath, SkillsManifest manifest, SkillsTarget target) {
        List<PendingPrune> stuck = [];
        foreach (var recorded in manifest.PendingPrunes ?? [])
            if (!PruneRecorded(recorded, target)) stuck.Add(recorded);
        SaveManifest(manifestPath, manifest with { Pending = false, PendingPrunes = [.. stuck] });
        return [.. stuck];
    }

    static async Task ReportStuckAsync(IReadOnlyList<PendingPrune> stuck) {
        foreach (var recorded in stuck)
            await Console.Error.WriteLineAsync(
                $"Could not remove {recorded.Path}: it does not resolve to a kcap directory under "
              + $"{recorded.Root}; it stays recorded for the next sync.");
    }

    SkillsManifest BuildManifest(
            string? etag, IReadOnlyList<SkillSnapshotItem> snapshot, string anchor,
            SkillsIdentity identity, SkillsTarget target, string repoHome, bool pending,
            IReadOnlyList<PendingPrune> journal) {
        var root = target.Root(anchor);
        return new() {
            Etag     = etag, SyncedAt = time.GetUtcNow(), Anchor = anchor, Identity = identity,
            Exposure = Exposure(target), Pending = pending, PendingPrunes = [.. journal],
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

    /// <summary>Reads a manifest, treating an unreadable or unparseable file as absent. A caller
    /// that must not act on a superseded copy holds the lock that owns the file first.</summary>
    static SkillsManifest? LoadQuietly(string path) {
        try {
            return File.Exists(path)
                ? JsonSerializer.Deserialize(File.ReadAllText(path), CapacitorJsonContext.Default.SkillsManifest)
                : null;
        } catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) {
            return null;
        }
    }

    static void SaveManifest(string path, SkillsManifest manifest) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.Replace(path, JsonSerializer.Serialize(manifest, CapacitorJsonContext.Default.SkillsManifest));
    }
}
