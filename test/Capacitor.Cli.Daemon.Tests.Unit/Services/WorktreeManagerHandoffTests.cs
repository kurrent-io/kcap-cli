using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

[ParallelLimiter<SubprocessLimit>]
public class WorktreeManagerHandoffTests {
    /// <summary>An upstream with one commit on its default branch and a clone of it; the clone is the
    /// repository a hand-off runs in, and the upstream is its origin.</summary>
    sealed class Fixture : IDisposable {
        readonly TempDir _cloneDir;

        Fixture(GitRepo upstream, GitRepo clone, TempDir cloneDir) {
            Upstream  = upstream;
            Clone     = clone;
            _cloneDir = cloneDir;
            Default   = upstream.CurrentBranch;
        }

        public GitRepo Upstream { get; }
        public GitRepo Clone    { get; }
        public string  Default  { get; }

        public static Fixture Create() {
            var upstream = GitRepo.Create("upstream");
            upstream.CreateFile("main.txt", "main");
            upstream.CommitAll("initial");
            var cloneDir = new TempDir("clone");

            return new Fixture(upstream, upstream.Clone(cloneDir.PathTo("repo")), cloneDir);
        }

        /// <summary>Adds a commit to <paramref name="branch"/> on origin, creating it from the default
        /// branch if needed, and returns the new tip.</summary>
        public string CommitOnOrigin(string branch, string file) {
            var exists = Upstream.Try("rev-parse", "--verify", "--quiet", $"refs/heads/{branch}").ExitCode == 0;
            Upstream.Checkout(branch, create: !exists);
            Upstream.CreateFile(file, file);
            Upstream.CommitAll(file);
            var tip = Upstream.Head;
            Upstream.Checkout(Default);

            return tip;
        }

        /// <summary>Creates the clone's local <paramref name="branch"/> at origin's current tip.</summary>
        public void TrackLocally(string branch) {
            Clone.Do("fetch", "-q", "origin");
            Clone.Do("branch", branch, $"origin/{branch}");
        }

        /// <summary>Adds a commit to the clone's local <paramref name="branch"/> and returns it.</summary>
        public string CommitLocally(string branch, string file) {
            Clone.Checkout(branch);
            Clone.CreateFile(file, file);
            Clone.CommitAll(file);
            var tip = Clone.Head;
            Clone.Checkout(Default);

            return tip;
        }

        public void Dispose() {
            Upstream.Dispose();
            _cloneDir.Dispose();
        }
    }

    static WorktreeManager Manager() =>
        new(new DaemonConfig(), NullLogger<WorktreeManager>.Instance, NoSnapshotBarrier.Instance, TimeProvider.System);

    static async Task<HandoffStart> ResolveAsync(Fixture f, HandoffTarget target) {
        var resolution = await Manager().ResolveHandoffStartAsync(f.Clone, target, CancellationToken.None);
        await Assert.That(resolution.FetchError).IsNull();

        return resolution.Start!;
    }

    [Test]
    public async Task branch_on_origin_without_a_local_branch_creates_it() {
        using var f   = Fixture.Create();
        var       tip = f.CommitOnOrigin("feature", "b.txt");

        var start = await ResolveAsync(f, new HandoffTarget("feature", null, false));

        await Assert.That(start.Source).IsEqualTo(HandoffSource.Branch);
        await Assert.That(start.Checkout).IsEqualTo(HandoffCheckout.CreateBranch);
        await Assert.That(start.Sha).IsEqualTo(tip);
        await Assert.That(start.Branch).IsEqualTo("feature");
        await Assert.That(start.LocalBranchSha).IsNull();
    }

    [Test]
    public async Task local_branch_equal_to_origin_is_checked_out() {
        using var f   = Fixture.Create();
        var       tip = f.CommitOnOrigin("feature", "b.txt");
        f.TrackLocally("feature");

        var start = await ResolveAsync(f, new HandoffTarget("feature", null, false));

        await Assert.That(start.Checkout).IsEqualTo(HandoffCheckout.CheckoutBranch);
        await Assert.That(start.Sha).IsEqualTo(tip);
        await Assert.That(start.LocalBranchSha).IsEqualTo(tip);
    }

    [Test]
    public async Task local_branch_strictly_behind_is_fast_forwarded() {
        using var f     = Fixture.Create();
        var       older = f.CommitOnOrigin("feature", "b.txt");
        f.TrackLocally("feature");
        var newer = f.CommitOnOrigin("feature", "c.txt");

        var start = await ResolveAsync(f, new HandoffTarget("feature", null, false));

        await Assert.That(start.Checkout).IsEqualTo(HandoffCheckout.FastForwardBranch);
        await Assert.That(start.Sha).IsEqualTo(newer);
        await Assert.That(start.LocalBranchSha).IsEqualTo(older);
    }

    [Test]
    public async Task local_branch_diverged_detaches() {
        using var f = Fixture.Create();
        f.CommitOnOrigin("feature", "b.txt");
        f.TrackLocally("feature");
        f.CommitLocally("feature", "local.txt");
        var remote = f.CommitOnOrigin("feature", "c.txt");

        var start = await ResolveAsync(f, new HandoffTarget("feature", null, false));

        await Assert.That(start.Checkout).IsEqualTo(HandoffCheckout.Detached);
        await Assert.That(start.DetachReason).IsEqualTo(HandoffDetachReason.LocalBranchDiverged);
        await Assert.That(start.Sha).IsEqualTo(remote);
        await Assert.That(start.Branch).IsEqualTo("feature");
    }

    [Test]
    public async Task local_branch_ahead_detaches() {
        using var f   = Fixture.Create();
        var       tip = f.CommitOnOrigin("feature", "b.txt");
        f.TrackLocally("feature");
        f.CommitLocally("feature", "local.txt");

        var start = await ResolveAsync(f, new HandoffTarget("feature", null, false));

        await Assert.That(start.Checkout).IsEqualTo(HandoffCheckout.Detached);
        await Assert.That(start.DetachReason).IsEqualTo(HandoffDetachReason.LocalBranchDiverged);
        await Assert.That(start.Sha).IsEqualTo(tip);
    }

    [Test]
    public async Task branch_checked_out_in_another_worktree_detaches() {
        using var f   = Fixture.Create();
        var       tip = f.CommitOnOrigin("feature", "b.txt");
        f.TrackLocally("feature");
        f.Clone.Do("worktree", "add", "-q", Path.Combine(f.Clone.Path, ".capacitor", "worktrees", "other"), "feature");

        var start = await ResolveAsync(f, new HandoffTarget("feature", null, false));

        await Assert.That(start.Checkout).IsEqualTo(HandoffCheckout.Detached);
        await Assert.That(start.DetachReason).IsEqualTo(HandoffDetachReason.CheckedOutElsewhere);
        await Assert.That(start.Sha).IsEqualTo(tip);
    }

    [Test]
    public async Task fork_pull_request_head_detaches_at_the_pull_ref() {
        using var f    = Fixture.Create();
        var       head = f.CommitOnOrigin("fork-work", "fork.txt");
        f.Upstream.Do("update-ref", "refs/pull/7/head", head);
        f.Upstream.Do("branch", "-D", "fork-work");

        var start = await ResolveAsync(f, new HandoffTarget("fork-work", 7, HeadIsFork: true));

        await Assert.That(start.Source).IsEqualTo(HandoffSource.ForkPullRequest);
        await Assert.That(start.Checkout).IsEqualTo(HandoffCheckout.Detached);
        await Assert.That(start.DetachReason).IsEqualTo(HandoffDetachReason.ForkHead);
        await Assert.That(start.Sha).IsEqualTo(head);
    }

    /// <summary>The clone still believes origin's default branch is the one it cloned; only origin itself
    /// knows it is now <c>trunk</c>.</summary>
    [Test]
    public async Task absent_branch_detaches_at_origins_default_branch() {
        using var f = Fixture.Create();
        f.Upstream.Do("branch", "-m", f.Default, "trunk");
        f.Upstream.CreateFile("t.txt", "t");
        f.Upstream.CommitAll("trunk commit");
        var trunk = f.Upstream.Head;

        var start = await ResolveAsync(f, new HandoffTarget("gone", null, false));

        await Assert.That(start.Source).IsEqualTo(HandoffSource.DefaultBranch);
        await Assert.That(start.Checkout).IsEqualTo(HandoffCheckout.Detached);
        await Assert.That(start.DetachReason).IsEqualTo(HandoffDetachReason.BranchAbsent);
        await Assert.That(start.Branch).IsEqualTo("trunk");
        await Assert.That(start.Sha).IsEqualTo(trunk);
    }

    [Test]
    public async Task unreachable_origin_is_a_fetch_error_not_an_absent_branch() {
        using var f = Fixture.Create();
        f.Clone.Do("remote", "set-url", "origin", Path.Combine(f.Clone.Path, "no-such-remote"));

        var resolution = await Manager().ResolveHandoffStartAsync(f.Clone, new HandoffTarget("feature", null, false), CancellationToken.None);

        await Assert.That(resolution.Start).IsNull();
        await Assert.That(resolution.FetchError).IsNotNull().And.IsNotEmpty();
    }

    [Test]
    public async Task hostile_branch_name_is_refused_before_git_runs() {
        using var f = Fixture.Create();

        await Assert.That(async () =>
                await Manager().ResolveHandoffStartAsync(f.Clone, new HandoffTarget("--upload-pack=touch x", null, false), CancellationToken.None))
            .Throws<InvalidOperationException>().WithMessageMatching("invalid_base_ref:*");
    }

    static async Task<WorktreeInfo> CreateAsync(Fixture f, HandoffStart start) =>
        await Manager().CreateHandoffAsync(f.Clone, start, "handoff");

    static string[] LocalBranches(GitRepo repo) =>
        repo.Do("for-each-ref", "--format=%(refname)", "refs/heads").Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    [Test]
    public async Task created_branch_tracks_origin_and_is_owned() {
        using var f     = Fixture.Create();
        f.CommitOnOrigin("feature", "b.txt");
        var       start = await ResolveAsync(f, new HandoffTarget("feature", null, false));
        var       tree  = await CreateAsync(f, start);

        try {
            var worktree = GitRepo.At(tree.Path);
            await Assert.That(worktree.Head).IsEqualTo(start.Sha);
            await Assert.That(worktree.CurrentBranch).IsEqualTo("feature");
            await Assert.That(worktree.Do("rev-parse", "--abbrev-ref", "feature@{upstream}").Text).IsEqualTo("origin/feature");
            await Assert.That(tree.OwnsBranch).IsTrue();
        } finally {
            await WorktreeManager.RemoveAsync(tree, TimeProvider.System);
        }
    }

    [Test]
    public async Task checked_out_branch_is_not_owned_and_survives_removal() {
        using var f = Fixture.Create();
        f.CommitOnOrigin("feature", "b.txt");
        f.TrackLocally("feature");
        var start = await ResolveAsync(f, new HandoffTarget("feature", null, false));
        var tree  = await CreateAsync(f, start);

        await Assert.That(GitRepo.At(tree.Path).Head).IsEqualTo(start.Sha);
        await Assert.That(tree.OwnsBranch).IsFalse();

        await WorktreeManager.RemoveAsync(tree, TimeProvider.System);

        await Assert.That(f.Clone.RevParse("refs/heads/feature")).IsEqualTo(start.Sha);
    }

    [Test]
    public async Task fast_forward_moves_the_local_branch_to_the_resolved_sha() {
        using var f = Fixture.Create();
        f.CommitOnOrigin("feature", "b.txt");
        f.TrackLocally("feature");
        f.CommitOnOrigin("feature", "c.txt");
        var start = await ResolveAsync(f, new HandoffTarget("feature", null, false));
        var tree  = await CreateAsync(f, start);

        try {
            await Assert.That(f.Clone.RevParse("refs/heads/feature")).IsEqualTo(start.Sha);
            await Assert.That(GitRepo.At(tree.Path).CurrentBranch).IsEqualTo("feature");
            await Assert.That(tree.OwnsBranch).IsFalse();
        } finally {
            await WorktreeManager.RemoveAsync(tree, TimeProvider.System);
        }
    }

    [Test]
    [Arguments(HandoffDetachReason.LocalBranchDiverged)]
    [Arguments(HandoffDetachReason.ForkHead)]
    [Arguments(HandoffDetachReason.BranchAbsent)]
    public async Task detached_start_creates_no_branch(HandoffDetachReason reason) {
        using var f = Fixture.Create();
        HandoffTarget target;

        switch (reason) {
            case HandoffDetachReason.LocalBranchDiverged:
                f.CommitOnOrigin("feature", "b.txt");
                f.TrackLocally("feature");
                f.CommitLocally("feature", "local.txt");
                target = new("feature", null, false);
                break;
            case HandoffDetachReason.ForkHead:
                f.Upstream.Do("update-ref", "refs/pull/3/head", f.CommitOnOrigin("fork-work", "fork.txt"));
                target = new("fork-work", 3, HeadIsFork: true);
                break;
            default:
                target = new("gone", null, false);
                break;
        }

        var start  = await ResolveAsync(f, target);
        var before = LocalBranches(f.Clone);
        var tree   = await CreateAsync(f, start);

        try {
            var worktree = GitRepo.At(tree.Path);
            await Assert.That(start.DetachReason).IsEqualTo(reason);
            await Assert.That(worktree.Try("symbolic-ref", "-q", "HEAD").ExitCode).IsNotEqualTo(0);
            await Assert.That(worktree.Head).IsEqualTo(start.Sha);
            await Assert.That(LocalBranches(f.Clone)).IsEquivalentTo(before);
            await Assert.That(tree.Branch).IsEqualTo("");
        } finally {
            await WorktreeManager.RemoveAsync(tree, TimeProvider.System);
        }
    }

    [Test]
    public async Task a_local_branch_that_moved_since_resolution_refuses() {
        using var f = Fixture.Create();
        f.CommitOnOrigin("feature", "b.txt");
        f.TrackLocally("feature");
        var start = await ResolveAsync(f, new HandoffTarget("feature", null, false));
        var moved = f.CommitLocally("feature", "later.txt");

        await Assert.That(async () => await CreateAsync(f, start))
            .Throws<InvalidOperationException>().WithMessageMatching("handoff_start_changed:*");

        await Assert.That(Directory.Exists(Path.Combine(f.Clone.Path, ".capacitor", "worktrees", "handoff"))).IsFalse();
        await Assert.That(f.Clone.RevParse("refs/heads/feature")).IsEqualTo(moved);
        await Assert.That(f.Clone.Try("rev-parse", "--verify", "--quiet", WorktreeManager.FetchedRefFor("handoff")).ExitCode).IsNotEqualTo(0);
    }

    [Test]
    public async Task a_branch_checked_out_since_resolution_refuses() {
        using var f = Fixture.Create();
        f.CommitOnOrigin("feature", "b.txt");
        f.TrackLocally("feature");
        f.CommitOnOrigin("feature", "c.txt");
        var start = await ResolveAsync(f, new HandoffTarget("feature", null, false));
        f.Clone.Do("worktree", "add", "-q", Path.Combine(f.Clone.Path, ".capacitor", "worktrees", "other"), "feature");

        await Assert.That(async () => await CreateAsync(f, start))
            .Throws<InvalidOperationException>().WithMessageMatching("handoff_start_changed:*");
    }

    /// <summary>A tree entry named <c>.git</c> cannot be checked out, so populating the worktree fails and
    /// creation rolls back.</summary>
    [Test]
    public async Task a_failed_checkout_rolls_back_without_deleting_the_existing_branch() {
        using var f    = Fixture.Create();
        var       blob = f.Upstream.DoWithInput("x"u8.ToArray(), "hash-object", "-w", "--stdin").Text;
        var       tree = f.Upstream.DoWithInput(System.Text.Encoding.UTF8.GetBytes($"100644 blob {blob}\t.git\n"), "mktree").Text;
        var       bad  = f.Upstream.Do("commit-tree", tree, "-p", "HEAD", "-m", "unreadable").Text;
        f.Upstream.Do("update-ref", "refs/heads/feature", bad);
        f.TrackLocally("feature");
        var start = await ResolveAsync(f, new HandoffTarget("feature", null, false));

        await Assert.That(start.Checkout).IsEqualTo(HandoffCheckout.CheckoutBranch);
        await Assert.That(async () => await CreateAsync(f, start)).Throws<InvalidOperationException>();

        await Assert.That(f.Clone.RevParse("refs/heads/feature")).IsEqualTo(bad);
        await Assert.That(Directory.Exists(Path.Combine(f.Clone.Path, ".capacitor", "worktrees", "handoff"))).IsFalse();
    }

    static Task<HandoffWork> CheckAsync(Fixture f, string startSha, IReadOnlyList<string>? commits, bool mayHaveUnpushedWork = false) =>
        Manager().CheckHandoffWorkAsync(f.Clone, startSha, commits, mayHaveUnpushedWork);

    [Test]
    public async Task all_session_commits_reachable_and_nothing_unpushed_is_complete() {
        using var f      = Fixture.Create();
        var       first  = f.CommitOnOrigin("feature", "b.txt");
        var       second = f.CommitOnOrigin("feature", "c.txt");
        var       start  = await ResolveAsync(f, new HandoffTarget("feature", null, false));

        var work = await CheckAsync(f, start.Sha, [first, second]);

        await Assert.That(work.Completeness).IsEqualTo(HandoffCompleteness.Complete);
        await Assert.That(work.MissingCommits).IsEmpty();
    }

    [Test]
    public async Task a_session_commit_missing_from_the_start_is_partly_missing() {
        using var f     = Fixture.Create();
        f.CommitOnOrigin("feature", "b.txt");
        f.TrackLocally("feature");
        var       local = f.CommitLocally("feature", "unpushed.txt");
        var       start = await ResolveAsync(f, new HandoffTarget("feature", null, false));

        var work = await CheckAsync(f, start.Sha, [local]);

        await Assert.That(work.Completeness).IsEqualTo(HandoffCompleteness.PartlyMissing);
        await Assert.That(work.MissingCommits).IsEquivalentTo([local]);
    }

    [Test]
    public async Task a_session_commit_unknown_locally_is_partly_missing() {
        using var f       = Fixture.Create();
        var       start   = await ResolveAsync(f, new HandoffTarget("gone", null, false));
        var       unknown = new string('a', 40);

        var work = await CheckAsync(f, start.Sha, [unknown]);

        await Assert.That(work.Completeness).IsEqualTo(HandoffCompleteness.PartlyMissing);
        await Assert.That(work.MissingCommits).IsEquivalentTo([unknown]);
    }

    [Test]
    public async Task unknown_session_commits_are_unconfirmed() {
        using var f     = Fixture.Create();
        var       start = await ResolveAsync(f, new HandoffTarget("gone", null, false));

        var work = await CheckAsync(f, start.Sha, null);

        await Assert.That(work.Completeness).IsEqualTo(HandoffCompleteness.Unconfirmed);
    }

    [Test]
    public async Task possible_unpushed_work_is_unconfirmed_even_when_commits_are_reachable() {
        using var f      = Fixture.Create();
        var       commit = f.CommitOnOrigin("feature", "b.txt");
        var       start  = await ResolveAsync(f, new HandoffTarget("feature", null, false));

        var work = await CheckAsync(f, start.Sha, [commit], mayHaveUnpushedWork: true);

        await Assert.That(work.Completeness).IsEqualTo(HandoffCompleteness.Unconfirmed);
    }

    [Test]
    public async Task a_malformed_commit_is_missing_and_never_reaches_git_as_an_option() {
        using var f     = Fixture.Create();
        var       start = await ResolveAsync(f, new HandoffTarget("gone", null, false));

        var work = await CheckAsync(f, start.Sha, ["--all"]);

        await Assert.That(work.Completeness).IsEqualTo(HandoffCompleteness.PartlyMissing);
        await Assert.That(work.MissingCommits).IsEquivalentTo(["--all"]);
    }

    [Test]
    public async Task an_untouched_fork_head_worktree_is_removed_with_its_fetched_ref() {
        using var f = Fixture.Create();
        f.Upstream.Do("update-ref", "refs/pull/5/head", f.CommitOnOrigin("fork-work", "fork.txt"));
        f.Upstream.Do("branch", "-D", "fork-work");
        var start = await ResolveAsync(f, new HandoffTarget("fork-work", 5, HeadIsFork: true));
        var tree  = await CreateAsync(f, start);

        var removal = await WorktreeManager.RemoveAsync(tree, TimeProvider.System);

        await Assert.That(removal).IsEqualTo(WorktreeRemoval.Removed);
        await Assert.That(f.Clone.Try("rev-parse", "--verify", "--quiet", tree.FetchedRef!).ExitCode).IsNotEqualTo(0);
    }

    /// <summary>A left-behind hand-off on the user's branch must leave that branch usable: deleting only
    /// the directory would leave git believing the branch is still checked out there.</summary>
    [Test]
    public async Task the_orphan_sweep_frees_a_branch_the_worktree_does_not_own() {
        using var f    = Fixture.Create();
        using var root = new TempDir();
        f.CommitOnOrigin("feature", "b.txt");
        f.TrackLocally("feature");
        var manager = new WorktreeManager(new DaemonConfig { AllowedRepoPaths = [f.Clone.Path], WorktreeRoot = root.Path },
            NullLogger<WorktreeManager>.Instance, NoSnapshotBarrier.Instance, TimeProvider.System);
        var start = (await manager.ResolveHandoffStartAsync(f.Clone, new HandoffTarget("feature", null, false), CancellationToken.None)).Start!;
        var tree  = await manager.CreateHandoffAsync(f.Clone, start, "left-behind");
        var work  = GitRepo.At(tree.Path);
        work.CreateFile("agent.txt", "agent");
        work.CommitAll("unpushed agent work");
        var agentCommit = work.Head;

        await manager.CleanupOrphanedAsync([]);

        await Assert.That(Directory.Exists(tree.Path)).IsFalse();
        await Assert.That(f.Clone.Try("checkout", "-q", "feature").ExitCode).IsEqualTo(0);
        await Assert.That(f.Clone.Head).IsEqualTo(agentCommit);
    }

    [Test]
    public async Task a_created_branch_in_a_single_branch_clone_records_its_upstream() {
        using var f = Fixture.Create();
        f.Clone.Do("config", "remote.origin.fetch", $"+refs/heads/{f.Default}:refs/remotes/origin/{f.Default}");
        f.CommitOnOrigin("feature", "b.txt");
        var start = await ResolveAsync(f, new HandoffTarget("feature", null, false));
        var tree  = await CreateAsync(f, start);

        try {
            await Assert.That(GitRepo.At(tree.Path).Head).IsEqualTo(start.Sha);
            await Assert.That(f.Clone.Do("config", "branch.feature.remote").Text).IsEqualTo("origin");
            await Assert.That(f.Clone.Do("config", "branch.feature.merge").Text).IsEqualTo("refs/heads/feature");
        } finally {
            await WorktreeManager.RemoveAsync(tree, TimeProvider.System);
        }
    }

    static WorktreeManager SweepingManager(Fixture f, TempDir root) =>
        new(new DaemonConfig { AllowedRepoPaths = [f.Clone.Path], WorktreeRoot = root.Path },
            NullLogger<WorktreeManager>.Instance, NoSnapshotBarrier.Instance, TimeProvider.System);

    /// <summary>No marker or record of the hand-off is needed: a daemon that died right after
    /// <c>worktree add</c> left nothing to say the branch was not its own.</summary>
    [Test]
    public async Task the_orphan_sweep_frees_a_user_branch_worktree_it_holds_no_record_of() {
        using var f    = Fixture.Create();
        using var root = new TempDir();
        f.CommitOnOrigin("feature", "b.txt");
        f.TrackLocally("feature");
        var path = Path.Combine(f.Clone.Path, ".capacitor", "worktrees", "crashed");
        f.Clone.Do("worktree", "add", "-q", path, "feature");

        await SweepingManager(f, root).CleanupOrphanedAsync([]);

        await Assert.That(Directory.Exists(path)).IsFalse();
        await Assert.That(f.Clone.Try("checkout", "-q", "feature").ExitCode).IsEqualTo(0);
    }

    [Test]
    public async Task the_orphan_sweep_keeps_a_user_branch_worktree_git_will_not_unregister() {
        using var f    = Fixture.Create();
        using var root = new TempDir();
        f.CommitOnOrigin("feature", "b.txt");
        f.TrackLocally("feature");
        var path = Path.Combine(f.Clone.Path, ".capacitor", "worktrees", "locked");
        f.Clone.Do("worktree", "add", "-q", path, "feature");
        f.Clone.Do("worktree", "lock", path);

        try {
            await SweepingManager(f, root).CleanupOrphanedAsync([]);

            await Assert.That(Directory.Exists(path)).IsTrue();
        } finally {
            f.Clone.Try("worktree", "unlock", path);
            f.Clone.Try("worktree", "remove", "--force", path);
        }
    }

    [Test]
    public async Task the_orphan_sweep_unregisters_the_daemons_own_worktrees_too() {
        using var f    = Fixture.Create();
        using var root = new TempDir();
        var manager = SweepingManager(f, root);
        var owned   = await manager.CreateAsync(f.Clone, name: "owned");

        await manager.CleanupOrphanedAsync([]);

        await Assert.That(Directory.Exists(owned.Path)).IsFalse();
        await Assert.That(f.Clone.Do("worktree", "list", "--porcelain").Text).DoesNotContain("owned");
    }

    [Test]
    public async Task a_cancelled_fork_resolution_fetches_nothing() {
        using var f = Fixture.Create();
        f.Upstream.Do("update-ref", "refs/pull/9/head", f.CommitOnOrigin("fork-work", "fork.txt"));

        await Assert.That(async () => await Manager().ResolveHandoffStartAsync(
                f.Clone, new HandoffTarget("fork-work", 9, HeadIsFork: true), new CancellationToken(canceled: true)))
            .Throws<OperationCanceledException>();
        await Assert.That(f.Clone.Try("rev-parse", "--verify", "--quiet", "refs/kcap/handoff/pull-9").ExitCode).IsNotEqualTo(0);
    }

    [Test]
    public async Task a_fork_target_without_a_pull_request_number_is_refused() {
        using var f = Fixture.Create();

        await Assert.That(async () => await Manager().ResolveHandoffStartAsync(
                f.Clone, new HandoffTarget("fork-work", null, HeadIsFork: true), CancellationToken.None))
            .Throws<InvalidOperationException>().WithMessageMatching("invalid_handoff_target:*");
    }

    /// <summary>A revert message cannot say whether a later revert of the revert put the work back, so
    /// the work is unconfirmed rather than missing — and never complete.</summary>
    [Test]
    [Arguments(1)]
    [Arguments(2)]
    public async Task a_reverted_session_commit_is_unconfirmed_though_reachable(int reverts) {
        using var f      = Fixture.Create();
        var       commit = f.CommitOnOrigin("feature", "b.txt");
        f.Upstream.Checkout("feature");
        f.Upstream.Do("revert", "--no-edit", commit);
        if (reverts == 2) f.Upstream.Do("revert", "--no-edit", "HEAD");
        f.Upstream.Checkout(f.Default);
        var start = await ResolveAsync(f, new HandoffTarget("feature", null, false));

        var work = await CheckAsync(f, start.Sha, [commit]);

        await Assert.That(work.Completeness).IsEqualTo(HandoffCompleteness.Unconfirmed);
        await Assert.That(work.MissingCommits).IsEmpty();
    }
}
