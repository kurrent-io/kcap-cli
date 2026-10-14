using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit;

public class PastedLinkDetectorTests {
    [Test]
    public async Task Detects_GitHub_PullRequest_URL_and_computes_RepoHash() {
        var match = PastedLinkDetector.TryDetect("Please check https://github.com/kurrent-io/kcap-cli/pull/428/files for regressions");

        await Assert.That(match).IsNotNull();
        await Assert.That(match!.Kind).IsEqualTo(PastedLinkKind.GitHubPullRequest);
        await Assert.That(match.Owner).IsEqualTo("kurrent-io");
        await Assert.That(match.Repo).IsEqualTo("kcap-cli");
        await Assert.That(match.Number).IsEqualTo(428);
        await Assert.That(match.RepoHash).IsEqualTo(RepoHashHelper.ComputeRepoHash("kurrent-io", "kcap-cli"));
        await Assert.That(match.RepoHash!.Length).IsEqualTo(16);
    }

    [Test]
    public async Task Detects_GitHub_Issue_URL() {
        var match = PastedLinkDetector.TryDetect("Fix https://github.com/kurrent-io/kcap-server/issues/91");

        await Assert.That(match).IsNotNull();
        await Assert.That(match!.Kind).IsEqualTo(PastedLinkKind.GitHubIssue);
        await Assert.That(match.Owner).IsEqualTo("kurrent-io");
        await Assert.That(match.Repo).IsEqualTo("kcap-server");
        await Assert.That(match.Number).IsEqualTo(91);
    }

    [Test]
    public async Task Detects_GitHub_Actions_Run_URL() {
        var match = PastedLinkDetector.TryDetect("Failing CI: https://github.com/kurrent-io/kcap-cli/actions/runs/9876543210/job/12345");

        await Assert.That(match).IsNotNull();
        await Assert.That(match!.Kind).IsEqualTo(PastedLinkKind.GitHubActionsRun);
        await Assert.That(match.Owner).IsEqualTo("kurrent-io");
        await Assert.That(match.Repo).IsEqualTo("kcap-cli");
        await Assert.That(match.RunId).IsEqualTo(9876543210L);
    }

    [Test]
    public async Task Detects_Linear_Issue_URL() {
        var match = PastedLinkDetector.TryDetect("Implement https://linear.app/kurrent/issue/AI-1725/whats-next-new-session-ux");

        await Assert.That(match).IsNotNull();
        await Assert.That(match!.Kind).IsEqualTo(PastedLinkKind.LinearIssue);
        await Assert.That(match.IssueKey).IsEqualTo("AI-1725");
    }

    [Test]
    public async Task LocalRepoHashIndex_resolves_repo_by_hash_and_by_pasted_link() {
        var index = new LocalRepoHashIndex(path => path switch {
            "/repos/kcap-cli"    => "git@github.com:kurrent-io/kcap-cli.git",
            "/repos/kcap-server" => "https://github.com/kurrent-io/kcap-server.git",
            _                    => null,
        });

        string[] repos = ["/repos/kcap-cli", "/repos/kcap-server"];

        var cliHash = RepoHashHelper.ComputeRepoHash("kurrent-io", "kcap-cli");
        await Assert.That(index.TryResolveByRepoHash(cliHash, repos)).IsEqualTo("/repos/kcap-cli");

        var prLink = PastedLinkDetector.TryDetect("https://github.com/kurrent-io/kcap-server/pull/19")!;
        await Assert.That(index.TryResolvePastedLink(prLink, repos)).IsEqualTo("/repos/kcap-server");
    }
}
