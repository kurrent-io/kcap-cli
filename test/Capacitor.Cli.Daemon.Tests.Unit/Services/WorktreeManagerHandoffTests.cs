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
}
