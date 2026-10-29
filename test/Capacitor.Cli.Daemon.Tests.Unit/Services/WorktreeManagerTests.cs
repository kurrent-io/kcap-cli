using System.Diagnostics;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Validates <see cref="WorktreeManager.CreateAsync"/> with a baseRef param.
/// We build a real local git repo with two commits on a side ref so we can
/// fetch it back as if it were a PR head and assert the worktree HEAD lines up.
/// </summary>
[ParallelLimiter<SubprocessLimit>]
public class WorktreeManagerTests {
    // A class, not a record: 'Clone' is disallowed as a record member name.
    sealed class Repo(GitRepo upstream, GitRepo clone, TempDir cloneDir) : IDisposable {
        public GitRepo Upstream { get; } = upstream;
        public GitRepo Clone    { get; } = clone;

        // Clone attaches to a directory cloneDir owns, so deleting that removes it.
        public void Dispose() {
            Upstream.Dispose();
            cloneDir.Dispose();
        }
    }

    static Repo MakeUpstreamWithSideRef(string sideRefName, out string sideCommitSha) {
        var upstream = GitRepo.Create("upstream");

        upstream.CreateFile("main.txt", "main");
        upstream.CommitAll("initial");

        var defaultBranch = upstream.CurrentBranch;

        // A second commit on a side branch, stored under a custom ref so the clone can fetch it like
        // a PR head.
        upstream.Checkout("side", create: true);
        upstream.CreateFile("side.txt", "side");
        upstream.CommitAll("side commit");
        sideCommitSha = upstream.Head;
        upstream.Do("update-ref", sideRefName, sideCommitSha);
        upstream.Checkout(defaultBranch);
        upstream.Do("branch", "-D", "side");

        // Allow `git clone` of a non-bare repo over the file:// protocol.
        upstream.Config("uploadpack.allowAnySHA1InWant", "true");

        // The clone is its own repository, so it gets its own directory rather than one inside the
        // upstream it came from.
        var cloneDir = new TempDir("clone");

        return new Repo(upstream, upstream.Clone(cloneDir.PathTo("repo")), cloneDir);
    }

    [Test]
    public async Task CreateAsync_WithBaseRef_WorktreeHeadMatchesFetchedCommit() {
        using var repo = MakeUpstreamWithSideRef("refs/pull/42/head", out var sideSha);

        var manager  = new WorktreeManager(new DaemonConfig(), NullLogger<WorktreeManager>.Instance, NoSnapshotBarrier.Instance, TimeProvider.System);
        var worktree = await manager.CreateAsync(repo.Clone, name: "review-pr-42", baseRef: "refs/pull/42/head");

        try {
            var head = GitRepo.At(worktree.Path).Head;

            await Assert.That(head).IsEqualTo(sideSha);
            await Assert.That(worktree.Branch).IsEqualTo("capacitor/review-pr-42");
        } finally {
            await WorktreeManager.RemoveAsync(worktree, TimeProvider.System);
        }
    }

    /// <summary>
    /// Fetching a head that moves a submodule makes git fetch the submodule too, from the submodule's own
    /// remote. That remote is the user's business, not the review's: unreachable here, it must not fail
    /// the launch.
    /// </summary>
    [Test]
    public async Task CreateAsync_fetches_a_head_that_moves_a_submodule_whose_remote_is_unreachable() {
        using var sub = GitRepo.CreateWithCommit("sub");
        using var up = GitRepo.CreateWithCommit("upstream");
        up.Do("-c", "protocol.file.allow=always", "submodule", "add", sub, "vendored");
        up.Commit("add submodule");
        up.Config("uploadpack.allowAnySHA1InWant", "true");

        using var cloneDir = new TempDir("clone");
        var clone = up.Clone(cloneDir.PathTo("repo"));
        clone.Do("-c", "protocol.file.allow=always", "submodule", "update", "--init");

        sub.CreateFile("moved.txt", "moved");
        sub.CommitAll("moved");
        var vendored = GitRepo.At(up.PathTo("vendored"));
        vendored.Do("fetch", "-q", "origin");
        vendored.Do("checkout", "-q", sub.Head);
        up.Add("vendored");
        up.Commit("move the submodule");
        up.Do("update-ref", "refs/pull/7/head", up.Head);
        var head = up.Head;
        // The clone's submodule remote must not exist: a recursing fetch that could reach the moved commit would make
        // the HEAD assertion prove nothing. Repointed rather than deleted, since Windows keeps git's read-only files.
        var missing = cloneDir.PathTo("no-such-remote");
        GitRepo.At(clone.PathTo("vendored")).Do("remote", "set-url", "origin", missing);
        clone.Do("config", "submodule.vendored.url", missing);
        await Assert.That(Directory.Exists(missing)).IsFalse();

        var manager  = new WorktreeManager(new DaemonConfig(), NullLogger<WorktreeManager>.Instance, NoSnapshotBarrier.Instance, TimeProvider.System);
        var worktree = await manager.CreateAsync(clone, name: "review-pr-7", baseRef: "refs/pull/7/head");

        try {
            await Assert.That(GitRepo.At(worktree.Path).Head).IsEqualTo(head);
        } finally {
            await WorktreeManager.RemoveAsync(worktree, TimeProvider.System);
        }
    }

    /// <summary>A commit is named whole when kcap's git hook recorded it, and by the abbreviation git printed when
    /// the server read it off a shell <c>git commit</c>.</summary>
    [Test]
    [Arguments(40)]
    [Arguments(7)]
    public async Task CreateAsync_with_a_local_only_sha_checks_it_out_without_a_remote(int shaLength) {
        using var repo = GitRepo.Create("local-only");

        repo.CreateFile("a.txt", "a");
        repo.CommitAll("first");
        var first = repo.Head;
        repo.CreateFile("b.txt", "b");
        repo.CommitAll("second");

        var manager  = new WorktreeManager(new DaemonConfig(), NullLogger<WorktreeManager>.Instance, NoSnapshotBarrier.Instance, TimeProvider.System);
        var worktree = await manager.CreateAsync(repo, name: "local-sha", baseRef: first[..shaLength]);

        try {
            await Assert.That(GitRepo.At(worktree.Path).Head).IsEqualTo(first);
            await Assert.That(worktree.FetchedRef).IsNull();
        } finally {
            await WorktreeManager.RemoveAsync(worktree, TimeProvider.System);
        }
    }

    /// <summary>A branch whose name is all hex starts from that branch, not from a same-named tag that rev-parse
    /// would resolve first when the name is read as a sha.</summary>
    [Test]
    public async Task CreateAsync_with_a_hex_named_branch_starts_from_the_branch_not_a_same_named_tag() {
        using var repo = GitRepo.Create("hex-branch");

        repo.CreateFile("a.txt", "a");
        repo.CommitAll("first");
        var first = repo.Head;
        repo.Do("branch", "cafe1234");
        repo.CreateFile("b.txt", "b");
        repo.CommitAll("second");
        repo.Do("tag", "cafe1234");

        var manager  = new WorktreeManager(new DaemonConfig(), NullLogger<WorktreeManager>.Instance, NoSnapshotBarrier.Instance, TimeProvider.System);
        var worktree = await manager.CreateAsync(repo, name: "hex-branch", baseRef: "cafe1234");

        try {
            await Assert.That(GitRepo.At(worktree.Path).Head).IsEqualTo(first);
        } finally {
            await WorktreeManager.RemoveAsync(worktree, TimeProvider.System);
        }
    }

    [Test]
    public async Task CreateAsync_with_an_unknown_sha_falls_back_to_the_origin_fetch() {
        using var repo = MakeUpstreamWithSideRef("refs/pull/9/head", out _);

        // Committed after the clone, so the clone's object store cannot have it.
        repo.Upstream.CreateFile("late.txt", "late");
        repo.Upstream.CommitAll("late commit");
        var lateSha = repo.Upstream.Head;
        repo.Upstream.Do("update-ref", "refs/pull/10/head", lateSha);

        var manager  = new WorktreeManager(new DaemonConfig(), NullLogger<WorktreeManager>.Instance, NoSnapshotBarrier.Instance, TimeProvider.System);
        var worktree = await manager.CreateAsync(repo.Clone, name: "remote-sha", baseRef: lateSha);

        try {
            await Assert.That(GitRepo.At(worktree.Path).Head).IsEqualTo(lateSha);
            await Assert.That(worktree.FetchedRef).IsEqualTo("refs/kcap/review/remote-sha");
        } finally {
            await WorktreeManager.RemoveAsync(worktree, TimeProvider.System);
        }
    }

    /// <summary>An abbreviation made on another machine and pushed is found among origin's branches, which a fetch
    /// of the abbreviation itself cannot do.</summary>
    [Test]
    public async Task CreateAsync_with_an_abbreviation_pushed_to_origin_starts_from_that_commit() {
        using var repo = MakeUpstreamWithSideRef("refs/pull/11/head", out _);

        // Committed after the clone, so the clone's object store cannot have it.
        repo.Upstream.CreateFile("pushed.txt", "pushed");
        repo.Upstream.CommitAll("pushed commit");
        var pushed = repo.Upstream.Head;

        var manager  = new WorktreeManager(new DaemonConfig(), NullLogger<WorktreeManager>.Instance, NoSnapshotBarrier.Instance, TimeProvider.System);
        var worktree = await manager.CreateAsync(repo.Clone, name: "pushed-short-sha", baseRef: pushed[..7]);

        try {
            await Assert.That(GitRepo.At(worktree.Path).Head).IsEqualTo(pushed);
            await Assert.That(worktree.FetchedRef).IsNull();
        } finally {
            await WorktreeManager.RemoveAsync(worktree, TimeProvider.System);
        }
    }

    [Test]
    public async Task CreateAsync_with_a_ref_name_keeps_the_fetch_path() {
        using var repo = MakeUpstreamWithSideRef("refs/pull/8/head", out _);

        var manager  = new WorktreeManager(new DaemonConfig(), NullLogger<WorktreeManager>.Instance, NoSnapshotBarrier.Instance, TimeProvider.System);
        var worktree = await manager.CreateAsync(repo.Clone, name: "ref-name", baseRef: "refs/pull/8/head");

        try {
            await Assert.That(worktree.FetchedRef).IsEqualTo("refs/kcap/review/ref-name");
        } finally {
            await WorktreeManager.RemoveAsync(worktree, TimeProvider.System);
        }
    }

    [Test]
    public async Task CreateAsync_with_a_local_branch_name_starts_from_that_branch_head() {
        using var repo = GitRepo.Create("local-branch");

        repo.CreateFile("a.txt", "a");
        repo.CommitAll("first");
        var defaultBranch = repo.CurrentBranch;
        repo.Checkout("feature/agent-work", create: true);
        repo.CreateFile("b.txt", "b");
        repo.CommitAll("agent commit");
        var branchHead = repo.Head;
        repo.Checkout(defaultBranch);

        var manager  = new WorktreeManager(new DaemonConfig(), NullLogger<WorktreeManager>.Instance, NoSnapshotBarrier.Instance, TimeProvider.System);
        var worktree = await manager.CreateAsync(repo, name: "local-branch", baseRef: "feature/agent-work");

        try {
            await Assert.That(GitRepo.At(worktree.Path).Head).IsEqualTo(branchHead);
            await Assert.That(worktree.FetchedRef).IsNull();
            await Assert.That(worktree.Branch).IsEqualTo("capacitor/local-branch");
        } finally {
            await WorktreeManager.RemoveAsync(worktree, TimeProvider.System);
        }
    }

    [Test]
    public async Task CreateAsync_with_a_branch_name_missing_locally_fetches_it_from_origin() {
        using var repo = MakeUpstreamWithSideRef("refs/pull/11/head", out var sideSha);

        // Created after the clone, so only origin has it.
        repo.Upstream.Do("branch", "remote-only", sideSha);

        var manager  = new WorktreeManager(new DaemonConfig(), NullLogger<WorktreeManager>.Instance, NoSnapshotBarrier.Instance, TimeProvider.System);
        var worktree = await manager.CreateAsync(repo.Clone, name: "remote-branch", baseRef: "remote-only");

        try {
            await Assert.That(GitRepo.At(worktree.Path).Head).IsEqualTo(sideSha);
            await Assert.That(worktree.FetchedRef).IsEqualTo("refs/kcap/review/remote-branch");
        } finally {
            await WorktreeManager.RemoveAsync(worktree, TimeProvider.System);
        }
    }

    [Test]
    [Arguments("bad..name")]
    [Arguments("-delete-me")]
    [Arguments("@{-1}")]
    [Arguments("with space")]
    public async Task CreateAsync_refuses_a_base_ref_that_is_not_a_valid_ref_name(string baseRef) {
        using var repo = GitRepo.Create("invalid-ref");

        repo.CreateFile("a.txt", "a");
        repo.CommitAll("first");

        var manager = new WorktreeManager(new DaemonConfig(), NullLogger<WorktreeManager>.Instance, NoSnapshotBarrier.Instance, TimeProvider.System);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await manager.CreateAsync(repo, name: "invalid-ref", baseRef: baseRef));

        await Assert.That(ex!.Message).Contains("invalid_base_ref");
        await Assert.That(Directory.Exists(Path.Combine(repo, ".capacitor", "worktrees", "invalid-ref"))).IsFalse();
    }

    public enum WorktreeState { Clean, ChangedFile, UntrackedFile, UniqueCommit, CommitOnAnotherBranch, DaemonEditsOnly, LinkToOverlay, LaunchWrittenConfig, LaunchConfigEditedAfter, AgentEditBeforeRelaunch, StagedConfigChange, CommitThenSwitchedAway }

    /// <summary>Removal never destroys work that exists only in the worktree: a change, an untracked
    /// file or a commit no other ref has, on HEAD or on the worktree's own branch, keeps the worktree and
    /// its branch. The daemon's own edits (the MCP config it deletes at creation, the config a launch
    /// writes, a file identical to the source checkout's) are not work; that config edited after the
    /// daemon recorded it is, as is a staged change to it, and so is a link, even to an identical file,
    /// since the link itself would be lost.</summary>
    [Test]
    [Arguments(WorktreeState.Clean, false)]
    [Arguments(WorktreeState.ChangedFile, true)]
    [Arguments(WorktreeState.UntrackedFile, true)]
    [Arguments(WorktreeState.UniqueCommit, true)]
    [Arguments(WorktreeState.CommitOnAnotherBranch, false)]
    [Arguments(WorktreeState.DaemonEditsOnly, false)]
    [Arguments(WorktreeState.LinkToOverlay, true)]
    [Arguments(WorktreeState.LaunchWrittenConfig, false)]
    [Arguments(WorktreeState.LaunchConfigEditedAfter, true)]
    [Arguments(WorktreeState.AgentEditBeforeRelaunch, true)]
    [Arguments(WorktreeState.StagedConfigChange, true)]
    [Arguments(WorktreeState.CommitThenSwitchedAway, true)]
    public async Task RemoveAsync_keeps_a_worktree_holding_work_that_exists_nowhere_else(WorktreeState state, bool kept) {
        using var repo = MakeUpstreamWithSideRef("refs/pull/1/head", out _);
        var source = repo.Clone;
        source.CreateFile(".mcp.json", "{}");
        source.CommitAll("ship an mcp config");
        source.CreateFile("notes.txt", "overlay");

        var manager  = new WorktreeManager(new DaemonConfig(), NullLogger<WorktreeManager>.Instance, NoSnapshotBarrier.Instance, TimeProvider.System);
        var worktree = await manager.CreateAsync(source);
        var tree     = GitRepo.At(worktree.Path);

        switch (state) {
            case WorktreeState.ChangedFile:
                tree.CreateFile("main.txt", "edited");
                break;
            case WorktreeState.UntrackedFile:
                tree.CreateFile("new.txt", "work");
                break;
            case WorktreeState.UniqueCommit:
                tree.CreateFile("new.txt", "work");
                tree.Do("add", "new.txt");
                tree.Commit("agent work");
                break;
            case WorktreeState.CommitThenSwitchedAway:
                tree.CreateFile("new.txt", "work");
                tree.Do("add", "new.txt");
                tree.Commit("agent work");
                tree.Do("checkout", "-q", "--detach", "HEAD~1");
                break;
            case WorktreeState.CommitOnAnotherBranch:
                tree.CreateFile("new.txt", "work");
                tree.Do("add", "new.txt");
                tree.Commit("agent work");
                source.Do("branch", "saved", tree.Head);
                break;
            case WorktreeState.DaemonEditsOnly:
                tree.CreateFile("notes.txt", "overlay");
                break;
            case WorktreeState.LaunchWrittenConfig:
            case WorktreeState.LaunchConfigEditedAfter:
            case WorktreeState.AgentEditBeforeRelaunch: {
                // As a launch's Prepare does: stamp, write the config, record.
                var before = WorktreeManager.StampDaemonPaths(worktree.Path);
                tree.CreateFile(".mcp.json", """{"mcpServers":{"kcap":{}}}""");
                tree.CreateFile(".claude/settings.local.json", """{"permissions":{"allow":["Bash"]}}""");
                WorktreeManager.RecordDaemonBaseline(worktree.Path, before);
                if (state != WorktreeState.LaunchWrittenConfig)
                    tree.CreateFile(".mcp.json", """{"mcpServers":{"kcap":{},"mine":{}}}""");
                if (state == WorktreeState.AgentEditBeforeRelaunch) {
                    // A resume prepares again and keeps the agent's edit; recording must not adopt it.
                    var again = WorktreeManager.StampDaemonPaths(worktree.Path);
                    WorktreeManager.RecordDaemonBaseline(worktree.Path, again);
                }
                break;
            }
            case WorktreeState.StagedConfigChange:
                tree.CreateFile(".mcp.json", """{"mcpServers":{"mine":{}}}""");
                tree.Do("add", ".mcp.json");
                break;
            case WorktreeState.LinkToOverlay:
                Skip.When(OperatingSystem.IsWindows(), "creating a symlink needs privileges Windows CI lacks");
                File.CreateSymbolicLink(Path.Combine(worktree.Path, "notes.txt"), source.PathTo("notes.txt"));
                break;
        }

        try {
            var removal = await WorktreeManager.RemoveAsync(worktree, TimeProvider.System);

            await Assert.That(removal.Kept).IsEqualTo(kept);
            await Assert.That(Directory.Exists(worktree.Path)).IsEqualTo(kept);
            await Assert.That(source.Try("rev-parse", "--verify", $"refs/heads/{worktree.Branch}").ExitCode == 0).IsEqualTo(kept);
        } finally {
            source.Try("worktree", "remove", "--force", worktree.Path);
            source.Try("branch", "-D", worktree.Branch);
        }
    }

    /// <summary>The startup sweep of left-behind worktrees keeps one holding work, as teardown does, and
    /// still clears one that holds none: a review worktree whose head only its fetched ref has, and a
    /// standalone snapshot, are the daemon's own.</summary>
    /// <summary>A commit on a branch the worktree does not own survives on that branch, so it is not work
    /// the removal would lose.</summary>
    [Test]
    public async Task RemoveAsync_keeps_a_branch_the_worktree_does_not_own() {
        using var repo = GitRepo.Create("not-owned");
        repo.CreateFile("a.txt", "a");
        repo.CommitAll("first");
        var main = repo.CurrentBranch;
        repo.Checkout("feature", create: true);
        repo.Checkout(main);

        var path = Path.Combine(repo.Path, ".capacitor", "worktrees", "handoff");
        repo.Do("worktree", "add", path, "feature");
        var tree = GitRepo.At(path);
        tree.CreateFile("b.txt", "b");
        tree.CommitAll("agent work");
        var agentCommit = tree.Head;

        var removal = await WorktreeManager.RemoveAsync(
            new WorktreeInfo(path, "feature", repo.Path) { OwnsBranch = false }, TimeProvider.System);

        await Assert.That(removal).IsEqualTo(WorktreeRemoval.Removed);
        await Assert.That(Directory.Exists(path)).IsFalse();
        await Assert.That(repo.Do("rev-parse", "refs/heads/feature").Text).IsEqualTo(agentCommit);
    }

    [Test]
    public async Task RemoveAsync_keeps_a_detached_worktree_holding_a_new_commit() {
        using var repo = GitRepo.Create("detached");
        repo.CreateFile("a.txt", "a");
        repo.CommitAll("first");

        var path = Path.Combine(repo.Path, ".capacitor", "worktrees", "detached");
        repo.Do("worktree", "add", "--detach", path, "HEAD");
        var tree = GitRepo.At(path);
        tree.CreateFile("b.txt", "b");
        tree.CommitAll("agent work");

        var removal = await WorktreeManager.RemoveAsync(
            new WorktreeInfo(path, "", repo.Path) { OwnsBranch = false }, TimeProvider.System);

        await Assert.That(removal.KeptBecause).IsNotNull();
        await Assert.That(Directory.Exists(path)).IsTrue();
    }

    [Test]
    public async Task CleanupOrphaned_keeps_a_left_behind_worktree_holding_work() {
        using var repo = MakeUpstreamWithSideRef("refs/pull/1/head", out _);
        using var worktreeRoot = new TempDir();
        var manager = new WorktreeManager(new DaemonConfig { AllowedRepoPaths = [repo.Clone.Path], WorktreeRoot = worktreeRoot.Path },
            NullLogger<WorktreeManager>.Instance, NoSnapshotBarrier.Instance, TimeProvider.System);
        var working = await manager.CreateAsync(repo.Clone, name: "working");
        var clean   = await manager.CreateAsync(repo.Clone, name: "clean");
        var review  = await manager.CreateAsync(repo.Clone, name: "review", baseRef: "refs/pull/1/head");
        GitRepo.At(working.Path).CreateFile("new.txt", "work");
        var standalone = Path.Combine(repo.Clone.Path, ".capacitor", "worktrees", "standalone");
        Directory.CreateDirectory(standalone);
        GitRepo.At(standalone).Do("init", "-q");
        GitRepo.At(standalone).Do("config", "user.email", "test@example.com");
        GitRepo.At(standalone).Do("config", "user.name", "Test");
        GitRepo.At(standalone).CreateFile("a.txt", "snapshot");
        GitRepo.At(standalone).CommitAll("Initial snapshot");

        try {
            await manager.CleanupOrphanedAsync([]);

            await Assert.That(Directory.Exists(working.Path)).IsTrue();
            await Assert.That(Directory.Exists(clean.Path)).IsFalse();
            await Assert.That(Directory.Exists(review.Path)).IsFalse();
            await Assert.That(Directory.Exists(standalone)).IsFalse();
        } finally {
            repo.Clone.Try("worktree", "remove", "--force", working.Path);
            repo.Clone.Try("worktree", "prune");
        }
    }

    [Test]
    public async Task CreateAsync_WithoutBaseRef_StillWorks() {
        using var repo = MakeUpstreamWithSideRef("refs/pull/1/head", out _);

        var manager  = new WorktreeManager(new DaemonConfig(), NullLogger<WorktreeManager>.Instance, NoSnapshotBarrier.Instance, TimeProvider.System);
        var worktree = await manager.CreateAsync(repo.Clone);

        try {
            await Assert.That(Directory.Exists(worktree.Path)).IsTrue();
            await Assert.That(worktree.Branch).StartsWith("capacitor/");
        } finally {
            await WorktreeManager.RemoveAsync(worktree, TimeProvider.System);
        }
    }

    /// <summary>
    /// Each fetch is routed into its own <c>refs/kcap/review/{name}</c> ref and worktree-added
    /// from that ref rather than the shared <c>FETCH_HEAD</c>, so concurrent launches against the
    /// same source repo cannot land on each other's commit. Asserts each worktree HEAD matches the
    /// SHA it asked for, with 5 launches issued in parallel.
    /// </summary>
    [Test]
    public async Task CreateAsync_ConcurrentBaseRefs_EachWorktreePinnedToCorrectSha() {
        using var upstream = GitRepo.Create("upstream");

        upstream.CreateFile("main.txt", "main");
        upstream.CommitAll("initial");

        var defaultBranch = upstream.CurrentBranch;

        // Build 5 distinct side commits, each saved as its own ref so the clone
        // can fetch them as if they were PR heads.
        const int concurrency = 5;
        var       refs        = new (string RefName, string Sha)[concurrency];

        for (var i = 0; i < concurrency; i++) {
            var refName = $"refs/pull/{100 + i}/head";
            upstream.Checkout($"side-{i}", create: true);
            upstream.CreateFile($"side-{i}.txt", $"side-{i}");
            upstream.CommitAll($"side {i}");
            var sha = upstream.Head;
            upstream.Do("update-ref", refName, sha);
            upstream.Checkout(defaultBranch);
            upstream.Do("branch", "-D", $"side-{i}");
            refs[i] = (refName, sha);
        }

        // The clone gets its own directory rather than one inside the upstream it came from.
        using var cloneDir = new TempDir("clone");
        var clone = upstream.Clone(cloneDir.PathTo("repo"));

        var manager   = new WorktreeManager(new DaemonConfig(), NullLogger<WorktreeManager>.Instance, NoSnapshotBarrier.Instance, TimeProvider.System);
        var worktrees = new WorktreeInfo[concurrency];

        await Task.WhenAll(
            Enumerable.Range(0, concurrency)
                .Select(async i => {
                        worktrees[i] = await manager.CreateAsync(clone, name: $"review-{i}", baseRef: refs[i].RefName);
                    }
                )
        );

        try {
            for (var i = 0; i < concurrency; i++) {
                var head = GitRepo.At(worktrees[i].Path).Head;
                await Assert.That(head).IsEqualTo(refs[i].Sha);
                await Assert.That(worktrees[i].FetchedRef).IsEqualTo($"refs/kcap/review/review-{i}");
            }
        } finally {
            foreach (var w in worktrees) {
                if (w is not null) await WorktreeManager.RemoveAsync(w, TimeProvider.System);
            }
        }
    }

    /// <summary>
    /// The fetched ref should be cleaned up by <see cref="WorktreeManager.RemoveAsync"/>
    /// so the source repo doesn't accumulate stale per-worktree refs after
    /// many review launches.
    /// </summary>
    [Test]
    public async Task RemoveAsync_DeletesFetchedRef() {
        using var repo = MakeUpstreamWithSideRef("refs/pull/77/head", out _);

        var manager  = new WorktreeManager(new DaemonConfig(), NullLogger<WorktreeManager>.Instance, NoSnapshotBarrier.Instance, TimeProvider.System);
        var worktree = await manager.CreateAsync(repo.Clone, name: "review-77", baseRef: "refs/pull/77/head");

        await Assert.That(worktree.FetchedRef).IsEqualTo("refs/kcap/review/review-77");

        // Sanity: ref exists before cleanup.
        var beforeRefs = repo.Clone.Do("for-each-ref", "refs/kcap/review/").Text;
        await Assert.That(beforeRefs).Contains("refs/kcap/review/review-77");

        await WorktreeManager.RemoveAsync(worktree, TimeProvider.System);

        var afterRefs = repo.Clone.Do("for-each-ref", "refs/kcap/review/").Text;
        await Assert.That(afterRefs).IsEmpty();
    }

    /// <summary>The submodule arrives as plain content — dirty and untracked files included, so this
    /// cannot pass against a pinned-commit checkout — and with no .git of its own.</summary>
    [Test]
    public async Task BorrowedSnapshot_CarriesSubmoduleFilesAsPlainContentWithoutItsGit() {
        using var subRepo = MakeUpstreamWithSideRef("refs/pull/91/head", out _);
        using var superRepo = MakeUpstreamWithSideRef("refs/pull/92/head", out _);
        using var tmp = new TempDir();
        var root = tmp.PathTo("root");

        subRepo.Clone.CreateFile("lib.txt", "sub-tracked");
        subRepo.Clone.Do("add", "lib.txt");
        subRepo.Clone.Do("commit", "-m", "sub content");

        superRepo.Clone.Do("-c", "protocol.file.allow=always", "submodule", "add", subRepo.Clone, "vendored");
        superRepo.Clone.Do("commit", "-m", "add submodule");

        // Dirty + untracked inside the submodule: the whole point of borrowing is that the
        // reviewer sees the developer's actual working tree, not the pinned commit.
        superRepo.Clone.CreateFile(["vendored", "lib.txt"], "sub-dirty");
        superRepo.Clone.CreateFile(["vendored", "scratch.txt"], "sub-untracked");

        var manager = new WorktreeManager(
            new DaemonConfig { WorktreeRoot = root }, NullLogger<WorktreeManager>.Instance, NoSnapshotBarrier.Instance, TimeProvider.System);
        var snapshot = await manager.CreateBorrowedSnapshotAsync(superRepo.Clone, "review", CancellationToken.None);

        try {
            await Assert.That(File.ReadAllText(Path.Combine(snapshot.Path, "vendored", "lib.txt")))
                .IsEqualTo("sub-dirty");
            await Assert.That(File.ReadAllText(Path.Combine(snapshot.Path, "vendored", "scratch.txt")))
                .IsEqualTo("sub-untracked");
            // No git identity for the submodule inside the snapshot, in either shape.
            await Assert.That(File.Exists(Path.Combine(snapshot.Path, "vendored", ".git"))).IsFalse();
            await Assert.That(Directory.Exists(Path.Combine(snapshot.Path, "vendored", ".git"))).IsFalse();
            // The superproject's own snapshot .git is still the independent one.
            await Assert.That(Directory.Exists(Path.Combine(snapshot.Path, ".git"))).IsTrue();
        } finally {
            await WorktreeManager.RemoveAsync(snapshot, TimeProvider.System);
        }
    }

    /// <summary>
    /// A gitlink whose working-tree directory is a SYMLINK must not cause git to be invoked outside
    /// the source tree. ContainedPath is lexical — Path.GetFullPath does not resolve links — so the
    /// escaped path passes containment while `git -C` follows the link and enumerates a directory the
    /// snapshot has no right to read. The per-file guards downstream cannot retroactively undo a
    /// directory-level traversal that already happened.
    /// </summary>
    [Test]
    public async Task BorrowedSnapshot_RefusesSubmodulePathThatIsASymlink() {
        using var repo = MakeUpstreamWithSideRef("refs/pull/93/head", out _);
        using var tmp = new TempDir();
        var outside = tmp.CreateDir("outside");
        var root    = tmp.PathTo("root");
        outside.CreateFile("secret.txt", "must-not-be-snapshotted");

        // Forge a gitlink entry whose path is a symlink to a directory outside the source.
        Directory.CreateSymbolicLink(repo.Clone.PathTo("vendored"), outside);
        repo.Clone.Do("update-index", "--add", "--cacheinfo", "160000," + new string('a', 40) + ",vendored");

        var manager = new WorktreeManager(
            new DaemonConfig { WorktreeRoot = root }, NullLogger<WorktreeManager>.Instance, NoSnapshotBarrier.Instance, TimeProvider.System);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await manager.CreateBorrowedSnapshotAsync(repo.Clone, "review", CancellationToken.None));
        await Assert.That(ex!.Message).StartsWith("borrowed_snapshot_symlink_unsupported");
    }

    [Test]
    public async Task BorrowedSnapshot_IsIndependent_CopiesDirtyContext_AndRefreshesPristinely() {
        using var repo = MakeUpstreamWithSideRef("refs/pull/88/head", out _);
        using var tmp = new TempDir();
        var root = tmp.PathTo("root");

        repo.Clone.CreateFile("main.txt", "dirty");
        repo.Clone.CreateFile("untracked.txt", "one");
        var manager = new WorktreeManager(
            new DaemonConfig { WorktreeRoot = root }, NullLogger<WorktreeManager>.Instance, NoSnapshotBarrier.Instance, TimeProvider.System);
        var snapshot = await manager.CreateBorrowedSnapshotAsync(repo.Clone, "review", CancellationToken.None);
        try {
            await Assert.That(snapshot.IsStandalone).IsTrue();
            await Assert.That(snapshot.Path.StartsWith(repo.Clone + Path.DirectorySeparatorChar, StringComparison.Ordinal)).IsFalse();
            await Assert.That(Directory.Exists(Path.Combine(snapshot.Path, ".git"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(snapshot.Path, ".git"))).IsFalse();
            await Assert.That(File.ReadAllText(Path.Combine(snapshot.Path, "main.txt"))).IsEqualTo("dirty");
            await Assert.That(File.ReadAllText(Path.Combine(snapshot.Path, "untracked.txt"))).IsEqualTo("one");
            await Assert.That(repo.Clone.Do("worktree", "list", "--porcelain").Text).DoesNotContain(snapshot.Path);

            File.WriteAllText(Path.Combine(snapshot.Path, "reviewer-created.txt"), "must disappear");
            File.WriteAllText(Path.Combine(snapshot.Path, ".git", "reviewer-metadata"), "must disappear");
            repo.Clone.CreateFile("untracked.txt", "two");
            await manager.SyncFromSourceAsync(repo.Clone, repo.Clone, snapshot.Path, [], CancellationToken.None);

            await Assert.That(File.Exists(Path.Combine(snapshot.Path, "reviewer-created.txt"))).IsFalse();
            await Assert.That(File.Exists(Path.Combine(snapshot.Path, ".git", "reviewer-metadata"))).IsFalse();
            await Assert.That(File.ReadAllText(Path.Combine(snapshot.Path, "untracked.txt"))).IsEqualTo("two");
            await Assert.That(File.ReadAllText(repo.Clone.PathTo("main.txt"))).IsEqualTo("dirty");
        } finally {
            await WorktreeManager.RemoveAsync(snapshot, TimeProvider.System);
        }
    }

    [Test]
    public async Task BorrowedSnapshot_RefreshPreservesRunningExecutionDirectory() {
        using var repo = MakeUpstreamWithSideRef("refs/pull/90/head", out _);
        using var tmp = new TempDir();
        var root = tmp.PathTo("root");
        Process? holder = null;
        var sourceCwd = repo.Clone.PathTo("src");
        Directory.CreateDirectory(sourceCwd);
        File.WriteAllText(Path.Combine(sourceCwd, "round.txt"), "one");
        var manager = new WorktreeManager(
            new DaemonConfig { WorktreeRoot = root }, NullLogger<WorktreeManager>.Instance, NoSnapshotBarrier.Instance, TimeProvider.System);
        var snapshot = await manager.CreateBorrowedSnapshotAsync(
            repo.Clone, sourceCwd, "review-subdir", CancellationToken.None);
        try {
            var isWindows = OperatingSystem.IsWindows();
            var psi = new ProcessStartInfo {
                // Launch the long-lived process directly so the tracked Process IS the one holding
                // snapshot.Path as its cwd. After a tree kill, WaitForExit reflects only the tracked
                // process; a wrapper (cmd.exe) whose child (ping.exe) is the real cwd owner could exit
                // first and leave that child still holding the directory on Windows — the very lock the
                // teardown's wait exists to clear. On Unix `exec` replaces the shell with sleep, so the
                // tracked pid stays the cwd owner there too.
                FileName = isWindows ? "ping.exe" : "/bin/sh",
                WorkingDirectory = snapshot.Path,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            if (isWindows) {
                psi.ArgumentList.Add("-n");
                psi.ArgumentList.Add("30");
                psi.ArgumentList.Add("127.0.0.1");
            } else {
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add("echo ready; exec sleep 30");
            }
            holder = Process.Start(psi);
            // The first non-empty stdout line proves a live process now holds snapshot.Path as its cwd:
            // wait for it rather than a fixed delay, since a loaded runner can start slowly. (ping emits
            // a leading blank line before its banner, so skip empties.)
            using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            string? firstLine;
            do { firstLine = await holder!.StandardOutput.ReadLineAsync(ready.Token); }
            while (firstLine is { Length: 0 });
            await Assert.That(firstLine).IsNotNull();

            File.WriteAllText(Path.Combine(sourceCwd, "round.txt"), "two");
            File.WriteAllText(Path.Combine(snapshot.Path, "reviewer-created.txt"), "remove");
            await manager.SyncFromSourceAsync(
                repo.Clone, sourceCwd, snapshot.SnapshotRoot!, [], CancellationToken.None);

            await Assert.That(holder!.HasExited).IsFalse();
            await Assert.That(Directory.Exists(snapshot.Path)).IsTrue();
            await Assert.That(File.ReadAllText(Path.Combine(snapshot.Path, "round.txt"))).IsEqualTo("two");
            await Assert.That(File.Exists(Path.Combine(snapshot.Path, "reviewer-created.txt"))).IsFalse();
        } finally {
            if (holder is { HasExited: false }) {
                holder.Kill(entireProcessTree: true);
                // Kill returns before the process releases its working-directory handle; on Windows the
                // snapshot it is standing in cannot be deleted until it has actually exited.
                await holder.WaitForExitAsync();
            }
            holder?.Dispose();
            await WorktreeManager.RemoveAsync(snapshot, TimeProvider.System);
        }
    }

    [Test]
    public async Task BorrowedSnapshot_RejectsSymlinkWithoutFollowingIt() {
        Skip.When(OperatingSystem.IsWindows(), "Symlink semantics in this certification are POSIX-only.");
        using var repo = MakeUpstreamWithSideRef("refs/pull/89/head", out _);
        using var tmp = new TempDir();
        var root = tmp.PathTo("root");

        File.CreateSymbolicLink(repo.Clone.PathTo("escape"), repo.Upstream.PathTo("main.txt"));
        var manager = new WorktreeManager(
            new DaemonConfig { WorktreeRoot = root }, NullLogger<WorktreeManager>.Instance, NoSnapshotBarrier.Instance, TimeProvider.System);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await manager.CreateBorrowedSnapshotAsync(repo.Clone, "review", CancellationToken.None));
        await Assert.That(ex!.Message).Contains("symlink_unsupported");
    }

    [Test]
    public async Task Snapshot_copy_rejects_linked_destination_parent_before_touching_target() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX symlink semantics.");
        using var tmp = new TempDir();
        var root = tmp.CreateDir("root");
        var external = tmp.CreateDir("external");
        var linkedParent = root.PathTo("linked");
        Directory.CreateSymbolicLink(linkedParent, external);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            WorktreeManager.EnsureParentDirectories(
                root, Path.Combine(linkedParent, "child", "file.txt")));

        await Assert.That(ex.Message)
            .StartsWith("borrowed_snapshot_destination_symlink_unsupported");
        await Assert.That(Directory.Exists(external.PathTo("child"))).IsFalse();
    }

    [Test]
    public async Task Snapshot_copy_rejects_linked_destination_leaf_without_truncating_target() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX symlink semantics.");
        using var tmp = new TempDir();
        var root = tmp.CreateDir("root");
        var external = Path.Combine(tmp.CreateDir("external"), "secret.txt");
        File.WriteAllText(external, "keep-me");
        var linkedLeaf = root.PathTo("file.txt");
        File.CreateSymbolicLink(linkedLeaf, external);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            WorktreeManager.EnsureDestinationLeafNoFollow(linkedLeaf));

        await Assert.That(ex.Message)
            .StartsWith("borrowed_snapshot_destination_symlink_unsupported");
        await Assert.That(File.ReadAllText(external)).IsEqualTo("keep-me");
    }

    [Test]
    public async Task Snapshot_build_rejects_branch_linked_parent_before_copying_dirty_child() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX symlink semantics.");
        using var repo = MakeUpstreamWithSideRef("refs/pull/91/head", out _);
        using var tmp = new TempDir();
        var root = tmp.PathTo("root");
        var external = tmp.CreateDir("external");

        var route = repo.Clone.PathTo("linked-parent");
        Directory.CreateSymbolicLink(route, external);
        repo.Clone.Do("add", "linked-parent");
        repo.Clone.Do("commit", "-q", "-m", "branch parent link");
        Directory.Delete(route);
        Directory.CreateDirectory(route);
        File.WriteAllText(Path.Combine(route, "dirty.txt"), "dirty working bytes");

        var manager = new WorktreeManager(
            new DaemonConfig { WorktreeRoot = root }, NullLogger<WorktreeManager>.Instance, NoSnapshotBarrier.Instance, TimeProvider.System);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await manager.CreateBorrowedSnapshotAsync(repo.Clone, "review", CancellationToken.None));

        await Assert.That(ex!.Message)
            .StartsWith("borrowed_snapshot_destination_symlink_unsupported");
        await Assert.That(File.Exists(external.PathTo("dirty.txt"))).IsFalse();
    }

    [Test]
    public async Task Snapshot_build_rejects_branch_linked_leaf_without_truncating_target() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX symlink semantics.");
        using var repo = MakeUpstreamWithSideRef("refs/pull/92/head", out _);
        using var tmp = new TempDir();
        var root = tmp.PathTo("root");
        var externalDir = tmp.CreateDir("external");
        var external = externalDir.PathTo("sentinel.txt");

        File.WriteAllText(external, "keep-me");
        var route = repo.Clone.PathTo("linked-leaf");
        File.CreateSymbolicLink(route, external);
        repo.Clone.Do("add", "linked-leaf");
        repo.Clone.Do("commit", "-q", "-m", "branch leaf link");
        File.Delete(route);
        File.WriteAllText(route, "dirty working bytes");

        var manager = new WorktreeManager(
            new DaemonConfig { WorktreeRoot = root }, NullLogger<WorktreeManager>.Instance, NoSnapshotBarrier.Instance, TimeProvider.System);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await manager.CreateBorrowedSnapshotAsync(repo.Clone, "review", CancellationToken.None));

        await Assert.That(ex!.Message)
            .StartsWith("borrowed_snapshot_destination_symlink_unsupported");
        await Assert.That(File.ReadAllText(external)).IsEqualTo("keep-me");
    }

    [Test]
    [Arguments("dir\\file.txt")]
    [Arguments("line\nbreak.txt")]
    [Arguments("café.txt")]
    public async Task Snapshot_paths_that_require_identity_rewriting_are_rejected(string path) {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            WorktreeManager.NormalizeRelativePath(path));
        await Assert.That(ex.Message).StartsWith("borrowed_snapshot_invalid_path");
    }

    [Test]
    public async Task Snapshot_path_validation_preserves_valid_replacement_character_exactly() {
        const string path = "replacement-�.txt";
        await Assert.That(WorktreeManager.NormalizeRelativePath(path)).IsEqualTo(path);
    }

    [Test]
    public async Task Snapshot_build_rejects_linked_source_parent_instead_of_copying_external_bytes() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX symlink semantics.");
        using var repo = MakeUpstreamWithSideRef("refs/pull/93/head", out _);
        using var tmp = new TempDir();
        var root = tmp.PathTo("root");
        var external = tmp.CreateDir("external");

        var trackedDir = repo.Clone.PathTo("tracked-dir");
        Directory.CreateDirectory(trackedDir);
        File.WriteAllText(Path.Combine(trackedDir, "child.txt"), "public");
        repo.Clone.Do("add", "tracked-dir/child.txt");
        repo.Clone.Do("commit", "-q", "-m", "tracked child");
        Directory.Delete(trackedDir, true);
        external.CreateFile("child.txt", "private external bytes");
        Directory.CreateSymbolicLink(trackedDir, external);

        var manager = new WorktreeManager(
            new DaemonConfig { WorktreeRoot = root }, NullLogger<WorktreeManager>.Instance, NoSnapshotBarrier.Instance, TimeProvider.System);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await manager.CreateBorrowedSnapshotAsync(repo.Clone, "review", CancellationToken.None));

        await Assert.That(ex!.Message).StartsWith("borrowed_snapshot_symlink_unsupported");
    }

    [Test]
    public async Task Snapshot_removes_stale_head_alias_using_destination_case_policy() {
        using var repo = MakeUpstreamWithSideRef("refs/pull/94/head", out _);
        using var tmp = new TempDir();
        WorktreeInfo? snapshot = null;

        Skip.When(!WorktreeManager.ProbeCaseSensitiveFileSystem(tmp.Path),
            "The stale spelling is distinct only on a case-sensitive filesystem.");

        repo.Clone.CreateFile("Alias.txt", "old committed spelling");
        repo.Clone.Do("add", "Alias.txt");
        repo.Clone.Do("commit", "-q", "-m", "add alias");
        repo.Clone.Do("mv", "Alias.txt", "alias.txt");
        repo.Clone.CreateFile("alias.txt", "new staged spelling");

        var manager = new WorktreeManager(
            new DaemonConfig { WorktreeRoot = tmp.Path }, NullLogger<WorktreeManager>.Instance, NoSnapshotBarrier.Instance, TimeProvider.System);
        snapshot = await manager.CreateBorrowedSnapshotAsync(
            repo.Clone, "review", CancellationToken.None);

        try {
            await Assert.That(File.Exists(Path.Combine(snapshot.Path, "Alias.txt"))).IsFalse();
            await Assert.That(File.ReadAllText(Path.Combine(snapshot.Path, "alias.txt")))
                .IsEqualTo("new staged spelling");
        } finally {
            if (snapshot is not null) await WorktreeManager.RemoveAsync(snapshot, TimeProvider.System);
        }
    }

    [Test]
    public async Task CleanupOrphaned_PreservesActiveBorrowedSnapshotContainerAndRemovesOnlyOrphans() {
        using var tmp = new TempDir();
        var root = tmp.PathTo("root");
        var activeRoot = Path.Combine(root, "borrowed-snapshots", "active");
        var activeCwd = Path.Combine(activeRoot, "src");
        var activeSidecar = WorktreeManager.ReviewContextRootFor(activeRoot);
        var orphan = Path.Combine(root, "borrowed-snapshots", "orphan");
        var orphanSidecar = WorktreeManager.ReviewContextRootFor(orphan);
        var legacy = Path.Combine(root, "legacy-orphan");
        Directory.CreateDirectory(activeCwd);
        Directory.CreateDirectory(activeSidecar);
        Directory.CreateDirectory(orphan);
        Directory.CreateDirectory(orphanSidecar);
        Directory.CreateDirectory(legacy);
        var manager = new WorktreeManager(
            new DaemonConfig { WorktreeRoot = root }, NullLogger<WorktreeManager>.Instance, NoSnapshotBarrier.Instance, TimeProvider.System);

        await manager.CleanupOrphanedAsync([activeCwd]);

        await Assert.That(Directory.Exists(activeRoot)).IsTrue();
        await Assert.That(Directory.Exists(activeSidecar)).IsTrue();
        await Assert.That(Directory.Exists(orphan)).IsFalse();
        await Assert.That(Directory.Exists(orphanSidecar)).IsFalse();
        await Assert.That(Directory.Exists(legacy)).IsFalse();
    }

    [Test]
    public async Task CleanupOrphaned_unlinks_borrowed_snapshot_root_without_following_target() {
        Skip.When(OperatingSystem.IsWindows(), "POSIX symlink semantics.");
        using var tmp = new TempDir();
        var root = tmp.CreateDir("root");
        var external = tmp.CreateDir("external");
        var borrowedSnapshots = root.PathTo("borrowed-snapshots");
        var externalChild = external.PathTo("must-survive");
        try {
            Directory.CreateDirectory(externalChild);
            File.WriteAllText(Path.Combine(externalChild, "sentinel.txt"), "keep-me");
            Directory.CreateSymbolicLink(borrowedSnapshots, external);
            var manager = new WorktreeManager(
                new DaemonConfig { WorktreeRoot = root }, NullLogger<WorktreeManager>.Instance, NoSnapshotBarrier.Instance, TimeProvider.System);

            await manager.CleanupOrphanedAsync();

            await Assert.That(Path.Exists(borrowedSnapshots)).IsFalse();
            await Assert.That(Directory.Exists(externalChild)).IsTrue();
            await Assert.That(File.ReadAllText(Path.Combine(externalChild, "sentinel.txt")))
                .IsEqualTo("keep-me");
        } finally {
            try { if (Path.Exists(borrowedSnapshots)) File.Delete(borrowedSnapshots); } catch { }
        }
    }

    /// <summary>A live launch's vendor state directory survives the sweep, and a dead one does not.
    ///
    /// <para>It holds the reviewer's whole <c>HOME</c> for the launch and is never itself an active
    /// worktree path, so the sweep's plain active-path rule would delete it mid-review — a failure that
    /// would present as an unreproducible vendor crash rather than as anything pointing here.</para></summary>
    [Test]
    public async Task CleanupOrphaned_KeepsALiveVendorStateDirectoryAndRemovesADeadOne() {
        using var tmp = new TempDir();
        var root       = tmp.PathTo("root");
        var snapshots  = Path.Combine(root, "borrowed-snapshots");
        var activeRoot = Path.Combine(snapshots, "active");
        var activeCwd  = Path.Combine(activeRoot, "src");
        var liveState  = WorktreeManager.VendorStateRootFor(activeRoot);
        var deadState  = WorktreeManager.VendorStateRootFor(Path.Combine(snapshots, "gone"));
        Directory.CreateDirectory(activeCwd);
        Directory.CreateDirectory(liveState);
        Directory.CreateDirectory(deadState);
        var manager = new WorktreeManager(
            new DaemonConfig { WorktreeRoot = root }, NullLogger<WorktreeManager>.Instance, NoSnapshotBarrier.Instance, TimeProvider.System);

        await manager.CleanupOrphanedAsync([activeCwd]);

        await Assert.That(Directory.Exists(liveState)).IsTrue()
            .Because("the running reviewer's HOME must survive the sweep");
        await Assert.That(Directory.Exists(deadState)).IsFalse()
            .Because("a state directory whose snapshot is gone is an orphan");
    }

    /// <summary>An active SNAPSHOT whose own name ends in the state-directory suffix is not deleted.
    ///
    /// <para>Snapshot directories are named from the agent id, so this shape is reachable. Treating the
    /// suffix as a classification — deriving an "owner" and testing only that — compares a live snapshot
    /// against a path that does not exist and deletes the reviewer's worktree. The suffix is a hint;
    /// the directory's own activeness is always checked first.</para></summary>
    [Test]
    public async Task CleanupOrphaned_KeepsAnActiveSnapshotWhoseNameEndsWithTheStateSuffix() {
        using var tmp = new TempDir();
        var root       = tmp.PathTo("root");
        var activeRoot = Path.Combine(root, "borrowed-snapshots", "borrowed-agent" + WorktreeManager.VendorStateSuffix);
        var activeCwd  = Path.Combine(activeRoot, "src");
        Directory.CreateDirectory(activeCwd);
        var manager = new WorktreeManager(
            new DaemonConfig { WorktreeRoot = root }, NullLogger<WorktreeManager>.Instance, NoSnapshotBarrier.Instance, TimeProvider.System);

        await manager.CleanupOrphanedAsync([activeCwd]);

        await Assert.That(Directory.Exists(activeCwd)).IsTrue()
            .Because("an active snapshot must survive regardless of what its name happens to end with");
    }

    /// Parallel checkout is a Windows-only change: macOS and Linux keep git's default checkout.
    [Test]
    public async Task Parallel_checkout_is_asked_for_on_windows_only() {
        await Assert.That(WorktreeManager.ParallelCheckoutFor(isWindows: true)).IsEquivalentTo([new GitConfigOverride("checkout.workers", "0")]);
        await Assert.That(WorktreeManager.ParallelCheckoutFor(isWindows: false)).IsEmpty();
    }
}
