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
/// kcap namespace inside the checkout's own harness trees, a per-(worktree, target) ledger in the
/// worktree's own git directory holds one row per physical path kcap owns and the credential it was
/// fetched under, and deletion walks those rows — never a skills root — so user-authored skills are
/// untouchable.
/// </summary>
class SkillsCommand(
        ConfigRoot config, HarnessRegistry harnesses, IRepositoriesApi repositories,
        GitProviderRouter router, WorkingDirectory workdir, TokenStore tokens,
        ProfileContext profiles, MachineAuth machine, LegacySkillsRoots legacy, TimeProvider time) {
    // The background refresh keys off each ledger's synced_at, so a burst of session starts
    // costs one network round-trip per interval per target, not one per session.
    static readonly TimeSpan AutoSyncInterval = TimeSpan.FromHours(6);

    internal const int Failed = 1;

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
            // A ledger that could not be reached is not one this repository stopped owning, so the
            // target is adopted and the read under the lock decides.
            .Where(t => Adopted(harnesses, t, Holds(LedgerPath(gitDir, t.Key)),
                                Holds(LegacyLedgerPath(hash, t.Key))))
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

    /// <summary>One target's sync. The attempt owns the per-worktree ledger lock for its whole life
    /// and has released it by the time it returns, because the lock the tail needs is shared and
    /// ordered outside it. <c>Settled</c> is false for an outcome that never reached a saved ledger,
    /// which is the one case the tail must not run on.
    ///
    /// <para>The machine-wide migration lock is needed only by a retirement, and whether this run is
    /// one is certain only once the ledger has been read under the inner lock — from where the outer
    /// lock can no longer be taken in order. So the lock-free read decides the first attempt, and an
    /// attempt that finds otherwise hands both locks back rather than holding a shared lock while it
    /// waits for a lock a peer legitimately holds across its fetch. One retry, so it
    /// terminates.</para></summary>
    async Task<int> SyncTargetAsync(
            SkillsTarget target, string anchor, string gitDir, string hash, string repoHome,
            SkillsIdentity identity, bool dryRun, bool auto) {
        var retiring = Retiring(ReadQuietly(LedgerPath(gitDir, target.Key), SkillOrigin.Repository),
                                ReadQuietly(LegacyLedgerPath(hash, target.Key), SkillOrigin.Legacy),
                                identity);

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
        var run = new SkillsSyncRun(config, repositories, time, target, anchor, gitDir, hash,
                                    repoHome, identity, dryRun, auto);

        // What an auto run may skip: a periodic refresh is exactly what a lock holder is already
        // doing. Classified from a read taken without the lock, so it is a hint about how long to
        // wait and never a decision to write. It canonicalizes an anchor, so it reads the
        // filesystem: a refusal there ends this target rather than escaping a run whose streams
        // nobody reads.
        bool outstanding;
        try {
            outstanding = auto && run.OutstandingFromPeek();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            await Console.Error.WriteLineAsync(Unwritable(target, anchor, ex));
            return (Failed, false, false);
        }

        // The only nesting: migration outside the per-worktree ledger lock, never the other way
        // round, and never with the repository lock, which the run took and released on its own
        // before any target started. Migration is released before the fetch: a shared lock must
        // never span a network request.
        var migrationLock = takeMigration ? TryAcquire(SkillsLocks.Migration) : null;
        if (takeMigration && migrationLock is null)
            return (await ContendedAsync("retiring global skills", target.Key, auto), false, false);

        // One sync per (worktree, target) at a time, machine-wide: a burst of session starts must
        // collapse to ONE refresh — the throttle alone cannot do that, since every child of the
        // burst reads the same stale synced_at before the winner stamps it. The ledger is read
        // UNDER this lock, so waiters see the winner's stamp.
        var ledgerLock = TryAcquire(SkillsLocks.Manifest(gitDir, target.Key),
                                    auto && !outstanding ? TimeSpan.FromMilliseconds(1) : null);
        if (ledgerLock is null) {
            migrationLock?.Dispose();
            if (auto) return (outstanding ? Incomplete : 0, false, false);
            await Console.Error.WriteLineAsync(
                $"Another kcap skills sync is already running for this repo ({target.Key}).");
            return (Failed, false, false);
        }
        using var heldLedgerLock = ledgerLock;

        try {
            var prologue = await run.PrepareAsync(migrationLock is not null);

            migrationLock?.Dispose();
            migrationLock = null;

            // A destination the filesystem refuses outright ends this target with every ownership
            // save it has already made intact, which is what the next run resolves from.
            return prologue ?? await run.ReconcileAsync();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            await Console.Error.WriteLineAsync(Unwritable(target, anchor, ex));
            return (Failed, false, false);
        } finally {
            migrationLock?.Dispose();
        }
    }

    /// <summary>The work that has to outlive the ledger lock: retiring the global copies under the
    /// machine-wide migration lock, which is shared and so is never taken while the ledger lock is
    /// held — which is also why it runs after the local ledger has been saved, the order migration
    /// requires.</summary>
    async Task<int> FinishTargetAsync(string hash, SkillsTarget target, string anchor,
                                      SkillsIdentity identity, bool auto) {
        var migration = TryAcquire(SkillsLocks.Migration);
        if (migration is null) return await ContendedAsync("retiring global skills", target.Key, auto);

        using (migration) {
            try {
                return await ReportRetirementAsync(SkillsLegacyMigration.Retire(
                    config.Directory, hash, target, identity, SkillDeletionCause.Superseded, retired: null));
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                await Console.Error.WriteLineAsync(Unwritable(target, anchor, ex));
                return Failed;
            }
        }
    }

    internal static async Task<int> ReportRetirementAsync(LegacyRetirement retirement) {
        foreach (var copy in retirement.Refused)
            await Console.Error.WriteLineAsync(
                $"Could not remove the global copy {copy}; it stays recorded for the next sync.");
        foreach (var copy in retirement.Unvouched)
            await Console.Error.WriteLineAsync(
                $"The global copy {copy} holds bytes no receipt kcap kept accounts for; it is reported and left alone.");
        // Never actionable, so never counted as work left undone — but the operator is the only one
        // who can decide what to do with a copy kcap will not touch again.
        foreach (var copy in retirement.Unverifiable)
            await Console.Error.WriteLineAsync(
                $"The global copy {copy} is a claim kcap cannot vouch for; it is never deleted.");

        return retirement.Incomplete ? Failed : 0;
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
                                 bool hasLedger, bool hasLegacyLedger) =>
        hasLedger || hasLegacyLedger || target.Consumers.Any(harnesses.Detected);

    internal static bool AutoThrottled(SkillsLedger? ledger, DateTimeOffset now) =>
        // A future stamp (clock correction, tampered file) must read as stale, not as an
        // unbounded suppression of every revocation refresh until that future arrives.
        ledger?.SyncedAt is { } syncedAt && now - syncedAt is { } age
            && age >= TimeSpan.Zero && age < AutoSyncInterval;

    /// <summary>Work the refresh throttle must not suppress: an unresolved operation, a reservation,
    /// a deletion still owed, a retired credential, a recorded legacy obligation, an anchor that
    /// moved, or a legacy ledger still holding something. The throttle exists to collapse a burst of
    /// periodic refreshes, and none of these is one — a refresh stamp is written before the tail that
    /// retires the global copies, so the local ledger on its own cannot tell a finished migration
    /// from an interrupted one.</summary>
    internal static bool Outstanding(SkillsLedger? ledger, bool legacyWork, SkillsIdentity identity,
                                     string anchor) =>
        legacyWork
        || (ledger is not null
            && (ledger.LegacyRetirement is not null
                || Superseded(ledger, identity)
                || ledger.Rows.Any(row => row.Prepared is not null
                                          || row.State is OwnedSkillState.Owed or OwnedSkillState.Reserved
                                          || Moved(row, anchor))));

    /// <summary>Whether either ledger records a credential that is not the current one, or a
    /// transition already recorded an obligation nothing has discharged. The legacy one counts
    /// because a target can be adopted on it alone, and a retirement nothing sees is one nothing
    /// carries out before the fetch — leaving the previous account's global copies loadable.
    /// </summary>
    static bool Retiring(SkillsLedger? local, SkillsLedger? legacy, SkillsIdentity identity) =>
        (Recorded(local?.Identity, Admitted(local)) is { } recorded && !Equals(recorded, identity))
        || Superseded(legacy, identity)
        || local?.LegacyRetirement is not null;

    /// <summary>The credential a ledger's catalogue belongs to: what the envelope recorded, or —
    /// for a ledger interrupted before it did — what an operation still in flight was authorised
    /// under. Only a row the validator admitted may answer, because this decides a retirement and a
    /// refused row must not be able to order one.</summary>
    internal static SkillsIdentity? Recorded(SkillsIdentity? envelope, IEnumerable<OwnedSkillRow> admitted) =>
        envelope ?? admitted.Select(row => row.Prepared?.Identity).FirstOrDefault(i => i is not null);

    static IEnumerable<OwnedSkillRow> Admitted(SkillsLedger? ledger) =>
        ledger is null ? [] : OwnedSkillRows.Adopt(ledger, (_, _) => { }).Live;

    /// <summary>A ledger recording no identity is adopted rather than retired: its files are still
    /// this profile's, and nothing contradicts them.</summary>
    static bool Superseded(SkillsLedger? ledger, SkillsIdentity identity) =>
        ledger?.Identity is not null && !Equals(ledger.Identity, identity);

    /// <summary>A repository row holding a copy written for an anchor that is not this run's —
    /// including a converted row, which records none at all. A settled or unverified row is never
    /// one: relocation will not move it and no run will ever finish it, so counting it would make
    /// the target outstanding at every session for good.</summary>
    static bool Moved(OwnedSkillRow row, string anchor) =>
        row is { Origin: SkillOrigin.Repository, State: OwnedSkillState.Published or OwnedSkillState.Owed }
        && (row.Anchor is null
            || !PathComparison.Equal(CanonicalPath.Resolve(row.Anchor), CanonicalPath.Resolve(anchor)));

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
    internal static string LedgerPath(string gitDir, string targetKey) =>
        Path.Combine(gitDir, "kcap", "skills", targetKey + ".json");

    string LegacyLedgerPath(string hash, string targetKey) =>
        SkillsLegacyMigration.ManifestPathFor(config.Directory, hash, targetKey);

    static bool Holds(string path) => PathExistence.OfFile(path) != PathPresence.Missing;

    static SkillsLedger? ReadQuietly(string path, SkillOrigin origin) =>
        SkillsLedgerFile.ReadQuietly(path, origin);

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

    /// <summary>Every harness measured to read the tree this target writes to — recorded because
    /// placement cannot enforce a vendor restriction for a tree several harnesses read.</summary>
    internal static string[] Exposure(SkillsTarget target) => [.. target.Readers.Select(r => r.VendorId)];

    IDisposable? TryAcquire(string name, TimeSpan? timeout = null) {
        try {
            return config.AcquireLock(name, timeout);
        } catch (TimeoutException) {
            return null;
        }
    }
}
