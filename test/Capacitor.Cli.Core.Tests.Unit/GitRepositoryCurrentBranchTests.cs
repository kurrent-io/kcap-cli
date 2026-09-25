namespace Capacitor.Cli.Core.Tests.Unit;

public class GitRepositoryCurrentBranchTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task A_main_checkout_reads_the_branch_from_its_head() {
        var repo = Tmp.CreateDir("repo");
        Tmp.CreateFile("repo/.git/HEAD", "ref: refs/heads/feature/x\n");

        await Assert.That(GitRepository.CurrentBranch(repo)).IsEqualTo("feature/x");
    }

    /// A linked worktree keeps its own HEAD under the main repository's worktrees entry, so a
    /// switch inside it is visible there and nowhere else.
    [Test]
    public async Task A_linked_worktree_reads_its_own_head_not_the_main_checkout() {
        Tmp.CreateFile("main/.git/HEAD", "ref: refs/heads/main\n");
        var worktree = Tmp.CreateDir("wt");
        Tmp.CreateFile("main/.git/worktrees/wt/HEAD", "ref: refs/heads/switched\n");
        Tmp.CreateFile("wt/.git", $"gitdir: {Tmp.PathTo("main/.git/worktrees/wt")}\n");

        await Assert.That(GitRepository.CurrentBranch(worktree)).IsEqualTo("switched");
    }

    [Test]
    public async Task A_detached_head_has_no_branch() {
        var repo = Tmp.CreateDir("repo");
        Tmp.CreateFile("repo/.git/HEAD", new string('a', 40) + "\n");

        await Assert.That(GitRepository.CurrentBranch(repo)).IsNull();
    }

    [Test]
    public async Task No_repository_has_no_branch() =>
        await Assert.That(GitRepository.CurrentBranch(Tmp.CreateDir("bare"))).IsNull();
}
