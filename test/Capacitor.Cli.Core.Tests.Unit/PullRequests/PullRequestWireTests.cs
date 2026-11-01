using Capacitor.Cli.Core.PullRequests;

namespace Capacitor.Cli.Core.Tests.Unit.PullRequests;

public class PullRequestWireTests {
    static readonly PullRequestSubjectDto Subject = new() {
        Provider = "github", Host = "github.com", RepoHash = "hash", Owner = "kurrent-io", RepoName = "kcap-cli", Number = 42,
    };

    [Test]
    [Arguments("https://github.com/kurrent-io/kcap-cli/pull/42")]
    [Arguments("https://github.com/kurrent-io/kcap/pull/42")]
    [Arguments("https://github.com/kurrent-io/kcap/pull/42/files")]
    [Arguments("https://github.com/kurrent-io/kcap/pull/42#issuecomment-1")]
    public async Task A_pr_link_under_a_renamed_repository_still_opens(string url) {
        await Assert.That(PullRequestWire.PrLink(url, Subject)).IsNotNull();
    }

    [Test]
    [Arguments("https://github.com/kurrent-io/kcap/pull/43")]
    [Arguments("https://github.com/kurrent-io/kcap/issues/42")]
    [Arguments("https://github.com/kurrent-io/kcap/pull/42/commits")]
    [Arguments("https://github.com/kurrent-io/pull/42")]
    [Arguments("https://gitlab.com/kurrent-io/kcap/pull/42")]
    [Arguments("http://github.com/kurrent-io/kcap/pull/42")]
    public async Task A_link_that_is_not_this_pr_on_its_host_is_refused(string url) {
        await Assert.That(PullRequestWire.PrLink(url, Subject)).IsNull();
    }
}
