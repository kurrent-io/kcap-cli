using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Behaviour tests for <see cref="Capacitor.Cli.Daemon.Services.RepoMatcher"/>. Each test creates a real
/// git repo with a controlled <c>origin</c> remote and asserts that
/// <see cref="Capacitor.Cli.Daemon.Services.RepoMatcher.FindAsync"/> returns the expected confirmed roots.
/// </summary>
public class RepoMatcherTests {
    static string MakeTempRepo(TempDir tmp, string dirName, string originUrl, string? subdir = null) {
        var root = tmp.CreateDir(dirName);

        GitRepo.At(root).Do("init", "-q");
        GitRepo.At(root).Do("remote", "add", "origin", originUrl);

        if (subdir is not null) {
            var sub = root.PathTo(subdir);
            Directory.CreateDirectory(sub);

            return sub;
        }

        return root;
    }

    static RepoMatcher NewMatcher() => new(new(), NullLogger<RepoMatcher>.Instance, TimeProvider.System);

    [Test]
    public async Task FindAsync_MatchingHttpsOrigin_ReturnsRoot() {
        using var tmp = new TempDir();
        var repo = MakeTempRepo(tmp, "repo", "https://github.com/contoso/widgets.git");
        var result = await NewMatcher().FindAsync("contoso", "widgets", [repo], CancellationToken.None);

        await Assert.That(result).Count().IsEqualTo(1);
        await Assert.That(result[0]).IsEqualTo(Path.GetFullPath(repo));
    }

    [Test]
    public async Task FindAsync_MatchingSshOrigin_ReturnsRoot() {
        using var tmp = new TempDir();
        var repo = MakeTempRepo(tmp, "repo", "git@github.com:contoso/widgets.git");
        var result = await NewMatcher().FindAsync("contoso", "widgets", [repo], CancellationToken.None);

        await Assert.That(result).Count().IsEqualTo(1);
    }

    [Test]
    public async Task FindAsync_DifferentOwner_ReturnsEmpty() {
        using var tmp = new TempDir();
        var repo = MakeTempRepo(tmp, "repo", "https://github.com/contoso/widgets.git");
        var result = await NewMatcher().FindAsync("other-org", "widgets", [repo], CancellationToken.None);

        await Assert.That(result).IsEmpty();
    }

    [Test]
    public async Task FindAsync_CandidateInsideRepo_WalksUpToRoot() {
        using var tmp = new TempDir();
        var sub = MakeTempRepo(tmp, "repo", "https://github.com/contoso/widgets.git", subdir: "src/Foo");
        var root = Path.GetFullPath(Path.Combine(sub, "..", ".."));
        var result = await NewMatcher().FindAsync("contoso", "widgets", [sub], CancellationToken.None);

        await Assert.That(result).Count().IsEqualTo(1);
        await Assert.That(result[0]).IsEqualTo(root);
    }

    [Test]
    public async Task FindAsync_MissingDirectory_Skipped() {
        using var ghostDir = TempDir.WithPathTo("definitely-not-here", out var ghost);

        var result = await NewMatcher().FindAsync("contoso", "widgets", [ghost], CancellationToken.None);

        await Assert.That(result).IsEmpty();
    }

    [Test]
    public async Task FindAsync_NonGitDirectory_Skipped() {
        using var tmp = new TempDir();
        var dir = tmp.CreateDir("not-a-repo");
        var result = await NewMatcher().FindAsync("contoso", "widgets", [dir], CancellationToken.None);

        await Assert.That(result).IsEmpty();
    }

    [Test]
    public async Task FindAsync_DuplicateCandidatesPointingAtSameRoot_DedupedToOne() {
        using var tmp = new TempDir();
        var sub = MakeTempRepo(tmp, "repo", "https://github.com/contoso/widgets.git", subdir: "src");
        var root = Path.GetFullPath(Path.Combine(sub, ".."));
        var result = await NewMatcher().FindAsync("contoso", "widgets", [sub, root, sub], CancellationToken.None);

        await Assert.That(result).Count().IsEqualTo(1);
    }

    [Test]
    public async Task FindAsync_MultipleDistinctCheckouts_ReturnsAll() {
        using var tmp = new TempDir();
        var repoA = MakeTempRepo(tmp, "repoA", "https://github.com/contoso/widgets.git");
        var repoB = MakeTempRepo(tmp, "repoB", "git@github.com:contoso/widgets.git");
        var result = await NewMatcher().FindAsync("contoso", "widgets", [repoA, repoB], CancellationToken.None);

        await Assert.That(result).Count().IsEqualTo(2);
        await Assert.That(result).Contains(Path.GetFullPath(repoA));
        await Assert.That(result).Contains(Path.GetFullPath(repoB));
    }

    [Test]
    public async Task FindAsync_OwnerCaseInsensitive() {
        using var tmp = new TempDir();
        var repo = MakeTempRepo(tmp, "repo", "https://github.com/Contoso/Widgets.git");
        var result = await NewMatcher().FindAsync("contoso", "WIDGETS", [repo], CancellationToken.None);

        await Assert.That(result).Count().IsEqualTo(1);
    }

    [Test]
    public async Task FindAsync_MatchingGitlabOrigin_ReturnsRoot() {
        using var tmp = new TempDir();
        var repo = MakeTempRepo(tmp, "repo", "git@gitlab.com:group/project.git");
        var result = await NewMatcher().FindAsync("group", "project", [repo], CancellationToken.None);

        await Assert.That(result).Contains(Path.GetFullPath(repo));
    }

    [Test]
    public async Task FindAsync_MatchingNestedGitlabGroupOrigin_ReturnsRoot() {
        using var tmp = new TempDir();
        // a nested namespace owner ("group/sub") must match the full
        // owner/repo suffix of the normalized remote.
        var repo = MakeTempRepo(tmp, "repo", "git@gitlab.com:group/sub/project.git");
        var result = await NewMatcher().FindAsync("group/sub", "project", [repo], CancellationToken.None);

        await Assert.That(result).Contains(Path.GetFullPath(repo));
    }

    [Test]
    public async Task FindAsync_NestedGroup_WrongSubgroup_ReturnsEmpty() {
        using var tmp = new TempDir();
        // A different subgroup with the same project name must NOT match.
        var repo = MakeTempRepo(tmp, "repo", "git@gitlab.com:group/sub/project.git");
        var result = await NewMatcher().FindAsync("group", "project", [repo], CancellationToken.None);

        await Assert.That(result).IsEmpty();
    }

    [Test]
    public async Task FindAsync_LinkedWorktreeCandidates_ResolveToTheMainCheckout() {
        using var main = GitRepo.CreateWithCommit();
        main.AddRemote("https://github.com/contoso/widgets.git");
        using var elsewhere = new TempDir();

        var agentWorktree    = main.AddWorktree(Path.Combine(main.Path, ".claude", "worktrees", "keen-moler"), "wt-agent");
        var externalWorktree = main.AddWorktree(elsewhere.PathTo("external"), "wt-external");

        var result = await NewMatcher().FindAsync(
            "contoso", "widgets", [agentWorktree.Path, externalWorktree.Path], CancellationToken.None, resolveWorktrees: true);

        await Assert.That(result).Count().IsEqualTo(1);
        await Assert.That(CanonicalPath.Resolve(result[0])).IsEqualTo(CanonicalPath.Resolve(main.Path));
    }

    /// <summary>Flow discovery picks a reviewer's checkout by exact path, and the reviewer branches
    /// from it — so unless the caller asks, a worktree must come back as itself.</summary>
    [Test]
    public async Task FindAsync_WithoutResolveWorktrees_ReturnsEachWorktreeAsItself() {
        using var main = GitRepo.CreateWithCommit();
        main.AddRemote("https://github.com/contoso/widgets.git");
        var worktree = main.AddWorktree(Path.Combine(main.Path, ".claude", "worktrees", "keen-moler"), "wt-agent");

        var result = await NewMatcher().FindAsync(
            "contoso", "widgets", [worktree.Path, main.Path], CancellationToken.None);

        await Assert.That(result.Select(CanonicalPath.Resolve))
            .IsEquivalentTo([CanonicalPath.Resolve(worktree.Path), CanonicalPath.Resolve(main.Path)]);
    }

    /// <summary>Flow discovery exact-matches the requester's repo root against what comes back, so
    /// unless worktrees are resolved each spelling of a checkout must come back as given.</summary>
    [Test]
    public async Task FindAsync_WithoutResolveWorktrees_KeepsEachSpellingOfACheckout() {
        using var tmp = new TempDir();
        var repo = MakeTempRepo(tmp, "repo", "https://github.com/contoso/widgets.git");
        var link = tmp.PathTo("repo-link");
        Directory.CreateSymbolicLink(link, repo);

        var result = await NewMatcher().FindAsync("contoso", "widgets", [repo, link], CancellationToken.None);

        await Assert.That(result).IsEquivalentTo([Path.GetFullPath(repo), Path.GetFullPath(link)]);
    }

    [Test]
    public async Task FindAsync_WorktreeAndMainCheckoutCandidates_DedupedToTheMainCheckout() {
        using var main = GitRepo.CreateWithCommit();
        main.AddRemote("https://github.com/contoso/widgets.git");
        var worktree = main.AddWorktree(Path.Combine(main.Path, ".capacitor", "worktrees", "agent-1"), "wt-agent");

        var result = await NewMatcher().FindAsync(
            "contoso", "widgets", [worktree.Path, main.Path], CancellationToken.None, resolveWorktrees: true);

        await Assert.That(result).Count().IsEqualTo(1);
        await Assert.That(CanonicalPath.Resolve(result[0])).IsEqualTo(CanonicalPath.Resolve(main.Path));
    }

    [Test]
    public async Task FindAsync_AllowedRepoPathsContributesCandidates() {
        using var tmp = new TempDir();
        var repo = MakeTempRepo(tmp, "repo", "https://github.com/contoso/widgets.git");
        var config = new DaemonConfig { AllowedRepoPaths = [repo] };
        var matcher = new RepoMatcher(config, NullLogger<RepoMatcher>.Instance, TimeProvider.System);

        // Pass empty server candidates — repo should still surface from AllowedRepoPaths.
        var result = await matcher.FindAsync("contoso", "widgets", [], CancellationToken.None);

        await Assert.That(result).Count().IsEqualTo(1);
    }
}
