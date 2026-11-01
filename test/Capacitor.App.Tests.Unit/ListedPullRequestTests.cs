using Capacitor.App.Services;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

/// The subject's provider must match the server's derivation, which accepts a github.com link to the
/// same PR number whatever owner and repo it names, so a session recorded under a repository's old
/// name still gets the subject the server's PR routes know.
public class ListedPullRequestTests {
    [Test]
    [Arguments("https://github.com/kurrent-io/kcap-cli/pull/7")]
    [Arguments("https://github.com/kurrent-io/kcap/pull/7")]
    [Arguments("https://github.com/kurrent-io/kcap/pull/7/files")]
    [Arguments("https://github.com/Other-Org/renamed/pull/7/")]
    public async Task A_github_link_to_the_same_PR_number_is_a_github_subject(string url) {
        var subject = ListedPullRequest.Subject(Listed(url));

        await Assert.That((subject.Provider, subject.Host, subject.Owner, subject.RepoName, subject.RepoHash, subject.Number))
            .IsEqualTo(("github", "github.com", "kurrent-io", "kcap-cli", "h", 7));
    }

    [Test]
    [Arguments("https://github.com/kurrent-io/kcap/pull/8", "github.com")]
    [Arguments("https://github.com/kurrent-io/kcap/issues/7", "github.com")]
    [Arguments("https://github.com/kurrent-io/kcap/commits/7", "github.com")]
    [Arguments("https://github.com/kurrent-io/kcap/pull/7/commits", "github.com")]
    [Arguments("https://gitlab.com/kurrent-io/kcap/pull/7", "gitlab.com")]
    [Arguments("http://github.com/kurrent-io/kcap/pull/7", "unknown")]
    public async Task Any_other_link_is_an_unknown_provider(string url, string host) {
        var subject = ListedPullRequest.Subject(Listed(url));

        await Assert.That((subject.Provider, subject.Host, subject.Owner, subject.RepoName))
            .IsEqualTo(("unknown", host, "kurrent-io", "kcap-cli"));
    }

    static RepoSessionPullRequestDto Listed(string url) => new() {
        RepoHash = "h", Owner = "Kurrent-IO", RepoName = "kcap-cli", Number = 7, Url = url, Title = "PR 7", State = "open" };
}
