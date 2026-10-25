using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.RepoEvidence;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Tests.Unit;

public class WatchCreatedPullRequestTests {
    static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    static string CreatedLine(int number) =>
        $$$"""{"type":"user","toolUseResult":{"stdout":"","gitOperation":{"pr":{"number":{{{number}}},"url":"https://github.com/kurrent-io/kcap-cli/pull/{{{number}}}","action":"created"""
      + "\"}}}}";

    static CreatedPullRequest Created(int number) => new("kurrent-io", "kcap-cli", number, $"https://github.com/kurrent-io/kcap-cli/pull/{number}");

    static Task Link(WatchState state, Func<CreatedPullRequest, TimeSpan, Task<PrInfo?>> describe, Func<RepositoryPayload, CancellationToken, Task<bool>> post) =>
        WatchCommand.LinkCreatedPullRequestsAsync(state, describe, post, Budget, TimeProvider.System, CancellationToken.None, () => { });

    static Task<PrInfo?> Describes(CreatedPullRequest pr, TimeSpan _) =>
        Task.FromResult<PrInfo?>(new PrInfo(pr.Number, $"Title {pr.Number}", pr.Url, $"head-{pr.Number}"));

    // Stacked PRs from one worktree are both linked, whichever branch is checked out.
    [Test]
    public async Task Every_created_pr_is_posted_with_its_title_and_head() {
        var state = new WatchState();
        WatchCommand.ObserveCreatedPullRequest(state, CreatedLine(1917));
        WatchCommand.ObserveCreatedPullRequest(state, CreatedLine(1918));
        var posted = new List<RepositoryPayload>();

        await Link(state, Describes, (pr, _) => { posted.Add(pr); return Task.FromResult(true); });

        await Assert.That(posted.Select(p => $"{p.Owner}/{p.RepoName}#{p.PrNumber} {p.PrTitle} {p.PrHeadRef}"))
            .IsEquivalentTo(["kurrent-io/kcap-cli#1917 Title 1917 head-1917", "kurrent-io/kcap-cli#1918 Title 1918 head-1918"]);
        await Assert.That(state.PendingCreatedPullRequests).IsEmpty();
        await Assert.That(state.LinkedPullRequests).Contains(("kurrent-io", "kcap-cli", 1918));
    }

    [Test]
    public async Task A_pr_gh_cannot_describe_is_still_linked_by_its_url() {
        var state = new WatchState();
        WatchCommand.ObserveCreatedPullRequest(state, CreatedLine(1917));
        var posted = new List<RepositoryPayload>();

        await Link(state, (_, _) => throw new InvalidOperationException("gh missing"), (pr, _) => { posted.Add(pr); return Task.FromResult(true); });

        await Assert.That(posted.Single().PrUrl).IsEqualTo("https://github.com/kurrent-io/kcap-cli/pull/1917");
        await Assert.That(posted.Single().PrTitle).IsNull();
    }

    [Test]
    public async Task A_rejected_post_stays_pending_for_the_next_pass() {
        var state = new WatchState();
        WatchCommand.ObserveCreatedPullRequest(state, CreatedLine(1917));

        await Link(state, Describes, (_, _) => Task.FromResult(false));

        await Assert.That(state.PendingCreatedPullRequests).IsEquivalentTo([Created(1917)]);
        await Assert.That(state.LinkedPullRequests).IsEmpty();
    }

    // The secondary-checkout probe may already have linked it, and a replay sees the line again.
    [Test]
    public async Task A_pr_already_linked_is_neither_queued_nor_posted_again() {
        var state = new WatchState();
        state.LinkedPullRequests.Add(("kurrent-io", "kcap-cli", 1917));
        WatchCommand.ObserveCreatedPullRequest(state, CreatedLine(1917));
        WatchCommand.ObserveCreatedPullRequest(state, CreatedLine(1918));
        WatchCommand.ObserveCreatedPullRequest(state, CreatedLine(1918));

        await Assert.That(state.PendingCreatedPullRequests).IsEquivalentTo([Created(1918)]);
    }
}
