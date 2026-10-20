using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Tests.Unit.PrDetection;

/// <summary>Real git answers the history probe; only gh is faked, listing one open PR whose head is
/// the <c>feature</c> tip.</summary>
[ParallelLimiter<SubprocessLimit>]
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

    /// <summary>The agent worktree the daemon cuts from the PR branch: the agent links nothing, and
    /// the PR branch keeps its link though another branch now holds its head.</summary>
    [Test]
    public async Task A_branch_cut_from_a_pr_branch_tip_is_not_that_pr() {
        using var repo = FeatureWithOpenPr(out var prHead);
        repo.Checkout("agent", create: true);

        await Assert.That(await Detect(repo, "agent", prHead)).IsNull();

        repo.Checkout("feature");
        await Assert.That((await Detect(repo, "feature", prHead))!.Number).IsEqualTo(9);
    }

    /// <summary>A copy carries the PR branch's reflog, creation entry included: only the copy entry
    /// marks where the copy itself starts.</summary>
    [Test]
    public async Task A_copy_of_a_pr_branch_is_not_that_pr() {
        using var repo = FeatureWithOpenPr(out var prHead);
        repo.Do("branch", "-c", "feature", "agent");
        repo.Checkout("agent");

        await Assert.That(await Detect(repo, "agent", prHead)).IsNull();
    }

    /// <summary>A rename keeps the reflog and stays the same branch, so it keeps its PR.</summary>
    [Test]
    public async Task A_renamed_pr_branch_keeps_its_pr() {
        using var repo = FeatureWithOpenPr(out var prHead);
        repo.Do("branch", "-m", "feature", "renamed");

        await Assert.That((await Detect(repo, "renamed", prHead))!.Number).IsEqualTo(9);
    }

    /// <summary>No local branch holds the PR head, as when the daemon fetches a base into its own
    /// ref: only the branch's creation point tells the two apart.</summary>
    [Test]
    public async Task A_branch_cut_from_a_fetched_pr_head_is_not_that_pr() {
        using var repo = FeatureWithOpenPr(out var prHead);
        repo.Do("update-ref", "refs/kcap/review/agent", prHead);
        repo.Checkout("main");
        repo.Do("branch", "-D", "feature");
        repo.Do("switch", "-q", "-c", "agent", "refs/kcap/review/agent");
        repo.Do("commit", "-q", "--allow-empty", "-m", "agent work");

        await Assert.That(await Detect(repo, "agent", prHead)).IsNull();
    }
}
