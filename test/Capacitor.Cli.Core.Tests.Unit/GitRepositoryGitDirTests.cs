namespace Capacitor.Cli.Core.Tests.Unit;

public class GitRepositoryGitDirTests {
    [TempDir] public required TempDir Tmp { get; init; }

    [Test]
    public async Task A_main_checkout_resolves_to_its_own_dot_git() {
        var repo = Tmp.CreateDir("repo");
        Tmp.CreateDir("repo/.git");

        await Assert.That(GitRepository.ResolveGitDir(repo))
            .IsEqualTo(Path.Combine(Tmp.GetResolvedPath("repo"), ".git"));
    }

    [Test]
    public async Task A_linked_worktree_resolves_to_its_worktrees_entry() {
        Tmp.CreateDir("main");
        var worktree = Tmp.CreateDir("wt");
        var entry    = Tmp.CreateDir("main/.git/worktrees/wt");
        Tmp.CreateFile("wt/.git", $"gitdir: {entry}\n");

        await Assert.That(GitRepository.ResolveGitDir(worktree))
            .IsEqualTo(Tmp.GetResolvedPath("main", ".git", "worktrees", "wt"));
    }

    [Test]
    public async Task A_main_checkout_and_its_linked_worktree_share_one_common_dir() {
        var main     = Tmp.CreateDir("main");
        Tmp.CreateDir("main/.git");
        var worktree = Tmp.CreateDir("wt");
        var entry    = Tmp.CreateDir("main/.git/worktrees/wt");
        Tmp.CreateFile("main/.git/worktrees/wt/commondir", "../..\n");
        Tmp.CreateFile("wt/.git", $"gitdir: {entry}\n");
        var unrelated = Tmp.CreateDir("other");
        Tmp.CreateDir("other/.git");

        await Assert.That(GitRepository.ResolveCommonDir(main)).IsEqualTo(Tmp.GetResolvedPath("main", ".git"));
        await Assert.That(GitRepository.ResolveCommonDir(worktree)).IsEqualTo(Tmp.GetResolvedPath("main", ".git"));
        await Assert.That(GitRepository.ResolveCommonDir(unrelated)).IsNotEqualTo(GitRepository.ResolveCommonDir(main));
    }

    [Test]
    public async Task No_repository_resolves_to_null() =>
        await Assert.That(GitRepository.ResolveGitDir(Tmp.CreateDir("bare"))).IsNull();
}
