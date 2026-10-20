using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Tests.Unit.PrDetection;

/// <summary>Real git answers the history probe; only gh is faked, listing one open PR whose head is
/// the <c>feature</c> tip.</summary>
public class HistoryPrDetectionTests {
    static GitRepo FeatureWithOpenPr(out string prHead) {
        var repo = GitRepo.CreateWithCommit();

        repo.AddRemote("git@github.com:acme/widget.git");
        repo.Do("update-ref", "refs/remotes/origin/main", "HEAD");
        repo.Do("symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");
        repo.Checkout("feature", create: true);
        repo.Do("commit", "-q", "--allow-empty", "-m", "pr head");
        prHead = repo.Head;

        return repo;
    }

    static Task<PrInfo?> Detect(GitRepo repo, string branch, string prHead) {
        var prs = $$"""[{"number":9,"title":"t","url":"u","headRefName":"fix/renamed","headRefOid":"{{prHead}}","isCrossRepository":false}]""";

        CommandRunner run = (cmd, args, cwd, cap) =>
            cmd == "gh" ? Task.FromResult<string?>(prs) : RepositoryDetection.DefaultRunner(TimeProvider.System)(cmd, args, cwd, cap);

        return GitHubPrDetector.DetectForHistoryAsync("github.com", "acme", "widget", branch, repo, () => TimeSpan.FromSeconds(5), run);
    }

    [Test]
    public async Task Finds_the_pr_when_only_this_branch_holds_its_head() {
        using var repo = FeatureWithOpenPr(out var prHead);

        await Assert.That((await Detect(repo, "feature", prHead))!.Number).IsEqualTo(9);
    }

    [Test]
    public async Task A_branch_cut_from_a_sibling_tip_is_not_the_sibling_pr() {
        using var repo = FeatureWithOpenPr(out var prHead);
        repo.Checkout("agent", create: true);

        await Assert.That(await Detect(repo, "agent", prHead)).IsNull();
    }
}
