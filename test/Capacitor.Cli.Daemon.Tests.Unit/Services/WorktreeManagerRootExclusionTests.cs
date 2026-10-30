using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>A launch keeps the worktree it makes under <c>.capacitor/worktrees/</c> out of the source checkout's
/// <c>git status</c>, through the repository's shared <c>info/exclude</c>, without disturbing what the user
/// wrote there. Removing a worktree leaves the exclusion in place for the next launch.</summary>
[ParallelLimiter<SubprocessLimit>]
public class WorktreeManagerRootExclusionTests {
    static WorktreeManager Manager() =>
        new(new DaemonConfig(), NullLogger<WorktreeManager>.Instance, NoSnapshotBarrier.Instance, TimeProvider.System);

    static string ExcludeOf(GitRepo repo) => File.ReadAllText(repo.PathTo(".git", "info", "exclude"));

    static int Count(string text, string line) =>
        text.Split('\n').Count(l => l.TrimEnd('\r') == line);

    [Test]
    public async Task Launches_leave_the_status_clean_and_add_the_line_once_after_the_users_own() {
        using var repo = GitRepo.CreateWithCommit("source");
        File.WriteAllText(repo.PathTo(".git", "info", "exclude"), "# mine\n*.log");
        var manager = Manager();

        var first = await manager.CreateAsync(repo, name: "agent-first");
        await Assert.That(repo.Status).IsEmpty();

        await WorktreeManager.RemoveAsync(first, TimeProvider.System);
        var second = await manager.CreateAsync(repo, name: "agent-second");
        var third  = await manager.CreateAsync(repo, name: "agent-third");

        try {
            await Assert.That(repo.Status).IsEmpty();
            var exclude = ExcludeOf(repo);
            await Assert.That(exclude).StartsWith("# mine\n*.log\n");
            await Assert.That(Count(exclude, "/.capacitor/worktrees/")).IsEqualTo(1);
        } finally {
            await WorktreeManager.RemoveAsync(second, TimeProvider.System);
            await WorktreeManager.RemoveAsync(third, TimeProvider.System);
        }
    }

    /// <summary>A linked worktree's <c>.git</c> is a file; the exclusion belongs in the common directory, which
    /// git reads for every worktree of the repository.</summary>
    [Test]
    public async Task A_launch_from_a_linked_worktree_writes_the_shared_exclude() {
        using var main   = GitRepo.CreateWithCommit("main");
        using var parent = new TempDir("linked");
        var linked = main.AddWorktree(parent.PathTo("wt"), "feature");

        var worktree = await Manager().CreateAsync(linked, name: "agent-linked");

        try {
            await Assert.That(linked.Status).IsEmpty();
            await Assert.That(File.Exists(linked.PathTo(".git"))).IsTrue();
            await Assert.That(Count(ExcludeOf(main), "/.capacitor/worktrees/")).IsEqualTo(1);
        } finally {
            await WorktreeManager.RemoveAsync(worktree, TimeProvider.System);
        }
    }

    [Test]
    public async Task A_launch_from_a_subdirectory_excludes_the_worktrees_under_it() {
        using var repo = GitRepo.CreateWithCommit("source");
        repo.CreateFile("sub/dir/file.txt", "x");
        repo.CommitAll("sub");

        var worktree = await Manager().CreateAsync(repo.PathTo("sub", "dir"), name: "agent-sub");

        try {
            await Assert.That(repo.Status).IsEmpty();
            await Assert.That(Count(ExcludeOf(repo), "/sub/dir/.capacitor/worktrees/")).IsEqualTo(1);
        } finally {
            await WorktreeManager.RemoveAsync(worktree, TimeProvider.System);
        }
    }

    /// <summary>A repository with no commit yet gets a standalone snapshot under the same root.</summary>
    [Test]
    public async Task A_standalone_snapshot_in_an_unborn_repository_is_excluded_too() {
        using var repo = GitRepo.Create("unborn");

        var worktree = await Manager().CreateAsync(repo, name: "agent-unborn");

        try {
            await Assert.That(worktree.IsStandalone).IsTrue();
            await Assert.That(repo.Status).IsEmpty();
        } finally {
            await WorktreeManager.RemoveAsync(worktree, TimeProvider.System);
        }
    }
}
