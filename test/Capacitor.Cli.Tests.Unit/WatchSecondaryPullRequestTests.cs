using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.RepoEvidence;

namespace Capacitor.Cli.Tests.Unit;

public class WatchSecondaryPullRequestTests {
    const string CliRoot = "/h/dev/cli-wt";

    static string? FakeFindRoot(string dir) => dir.StartsWith(CliRoot, StringComparison.Ordinal) ? CliRoot : null;

    static WatchState StateWithSecondaryRoot() {
        var state = new WatchState { SecondaryRoots = new SecondaryRepoRoots(FakeFindRoot, "/h/dev/server") };
        state.SecondaryRoots.OnLine("claude",
            """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Edit","input":{"file_path":"/h/dev/cli-wt/src/x.cs"}}]}}""");
        return state;
    }

    static RepositoryPayload CliPr(int number = 915, string host = "github.com") => new() {
        Host = host, Owner = "kurrent-io", RepoName = "kcap-cli", Branch = "feature", PrNumber = number, PrUrl = $"https://{host}/kurrent-io/kcap-cli/pull/{number}"
    };

    [Test]
    public async Task A_detected_pr_is_posted_once() {
        var state  = StateWithSecondaryRoot();
        var probed = new List<string>();
        var posted = new List<RepositoryPayload>();

        for (var pass = 0; pass < 2; pass++) {
            await WatchCommand.LinkSecondaryPullRequestsAsync(state,
                root => { probed.Add(root); return Task.FromResult<RepositoryPayload?>(CliPr()); },
                pr => { posted.Add(pr); return Task.FromResult(true); });
        }

        await Assert.That(probed).IsEquivalentTo([CliRoot, CliRoot]);
        await Assert.That(posted).Count().IsEqualTo(1);
        await Assert.That(posted[0].PrNumber).IsEqualTo(915);
    }

    [Test]
    public async Task A_failed_post_is_retried_on_the_next_pass() {
        var state = StateWithSecondaryRoot();
        var posts = 0;

        Task<bool> Post(RepositoryPayload _) => Task.FromResult(++posts == 2);

        await WatchCommand.LinkSecondaryPullRequestsAsync(state, _ => Task.FromResult<RepositoryPayload?>(CliPr()), Post);
        await WatchCommand.LinkSecondaryPullRequestsAsync(state, _ => Task.FromResult<RepositoryPayload?>(CliPr()), Post);
        await WatchCommand.LinkSecondaryPullRequestsAsync(state, _ => Task.FromResult<RepositoryPayload?>(CliPr()), Post);

        await Assert.That(posts).IsEqualTo(2);
    }

    [Test]
    public async Task A_checkout_without_a_pr_is_probed_again_but_never_posted() {
        var state  = StateWithSecondaryRoot();
        var probed = 0;
        var posted = 0;
        var noPr   = CliPr() with { PrNumber = null, PrUrl = null };

        await WatchCommand.LinkSecondaryPullRequestsAsync(state,
            _ => { probed++; return Task.FromResult<RepositoryPayload?>(noPr); },
            _ => { posted++; return Task.FromResult(true); });
        await WatchCommand.LinkSecondaryPullRequestsAsync(state,
            _ => { probed++; return Task.FromResult<RepositoryPayload?>(noPr); },
            _ => { posted++; return Task.FromResult(true); });

        await Assert.That(probed).IsEqualTo(2);
        await Assert.That(posted).IsEqualTo(0);
    }

    [Test]
    public async Task A_pr_on_a_non_github_host_is_not_posted() {
        var state  = StateWithSecondaryRoot();
        var posted = 0;

        await WatchCommand.LinkSecondaryPullRequestsAsync(state,
            _ => Task.FromResult<RepositoryPayload?>(CliPr(host: "gitlab.com")),
            _ => { posted++; return Task.FromResult(true); });

        await Assert.That(posted).IsEqualTo(0);
    }

    [Test]
    public async Task A_new_pr_on_the_same_checkout_is_posted_too() {
        var state  = StateWithSecondaryRoot();
        var posted = new List<int>();

        await WatchCommand.LinkSecondaryPullRequestsAsync(state, _ => Task.FromResult<RepositoryPayload?>(CliPr(915)),
            pr => { posted.Add(pr.PrNumber!.Value); return Task.FromResult(true); });
        await WatchCommand.LinkSecondaryPullRequestsAsync(state, _ => Task.FromResult<RepositoryPayload?>(CliPr(916)),
            pr => { posted.Add(pr.PrNumber!.Value); return Task.FromResult(true); });

        await Assert.That(posted).IsEquivalentTo([915, 916]);
    }

    [Test]
    public async Task A_watcher_without_a_collector_probes_nothing() {
        var probed = 0;

        await WatchCommand.LinkSecondaryPullRequestsAsync(new WatchState(),
            _ => { probed++; return Task.FromResult<RepositoryPayload?>(CliPr()); },
            _ => Task.FromResult(true));

        await Assert.That(probed).IsEqualTo(0);
    }
}
