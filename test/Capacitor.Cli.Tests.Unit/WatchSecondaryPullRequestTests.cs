using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.RepoEvidence;

namespace Capacitor.Cli.Tests.Unit;

public class WatchSecondaryPullRequestTests {
    const string CliRoot = "/h/dev/cli-wt";

    static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

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

    static Func<string, TimeSpan, Task<RepositoryPayload?>> Detects(RepositoryPayload? repo) => (_, _) => Task.FromResult(repo);

    static Task Link(WatchState state, Func<string, TimeSpan, Task<RepositoryPayload?>> detect, Func<RepositoryPayload, CancellationToken, Task<bool>> post,
        TimeSpan? budget = null, CancellationToken ct = default) =>
        WatchCommand.LinkSecondaryPullRequestsAsync(state, detect, post, budget ?? Budget, ct);

    [Test]
    public async Task A_detected_pr_is_posted_once() {
        var state  = StateWithSecondaryRoot();
        var probed = new List<string>();
        var posted = new List<RepositoryPayload>();

        for (var pass = 0; pass < 2; pass++) {
            await Link(state,
                (root, _) => { probed.Add(root); return Task.FromResult<RepositoryPayload?>(CliPr()); },
                (pr, _) => { posted.Add(pr); return Task.FromResult(true); });
        }

        await Assert.That(probed).IsEquivalentTo([CliRoot, CliRoot]);
        await Assert.That(posted).Count().IsEqualTo(1);
        await Assert.That(posted[0].PrNumber).IsEqualTo(915);
    }

    [Test]
    public async Task A_failed_post_is_retried_on_the_next_pass() {
        var state = StateWithSecondaryRoot();
        var posts = 0;

        Task<bool> Post(RepositoryPayload _, CancellationToken __) => Task.FromResult(++posts == 2);

        await Link(state, Detects(CliPr()), Post);
        await Link(state, Detects(CliPr()), Post);
        await Link(state, Detects(CliPr()), Post);

        await Assert.That(posts).IsEqualTo(2);
    }

    [Test]
    public async Task A_checkout_without_a_pr_is_probed_again_but_never_posted() {
        var state  = StateWithSecondaryRoot();
        var probed = 0;
        var posted = 0;
        var noPr   = CliPr() with { PrNumber = null, PrUrl = null };

        for (var pass = 0; pass < 2; pass++) {
            await Link(state,
                (_, _) => { probed++; return Task.FromResult<RepositoryPayload?>(noPr); },
                (_, _) => { posted++; return Task.FromResult(true); });
        }

        await Assert.That(probed).IsEqualTo(2);
        await Assert.That(posted).IsEqualTo(0);
    }

    [Test]
    public async Task A_pr_on_a_non_github_host_is_not_posted() {
        var state  = StateWithSecondaryRoot();
        var posted = 0;

        await Link(state, Detects(CliPr(host: "gitlab.com")), (_, _) => { posted++; return Task.FromResult(true); });

        await Assert.That(posted).IsEqualTo(0);
    }

    [Test]
    public async Task A_new_pr_on_the_same_checkout_is_posted_too() {
        var state  = StateWithSecondaryRoot();
        var posted = new List<int>();

        Task<bool> Post(RepositoryPayload pr, CancellationToken _) { posted.Add(pr.PrNumber!.Value); return Task.FromResult(true); }

        await Link(state, Detects(CliPr(915)), Post);
        await Link(state, Detects(CliPr(916)), Post);

        await Assert.That(posted).IsEquivalentTo([915, 916]);
    }

    [Test]
    public async Task A_watcher_without_a_collector_probes_nothing() {
        var probed = 0;

        await Link(new WatchState(), (_, _) => { probed++; return Task.FromResult<RepositoryPayload?>(CliPr()); }, (_, _) => Task.FromResult(true));

        await Assert.That(probed).IsEqualTo(0);
    }

    [Test]
    public async Task Each_probe_is_handed_no_more_than_the_pass_budget() {
        var state   = StateWithSecondaryRoot();
        var budgets = new List<TimeSpan>();

        await Link(state, (_, budget) => { budgets.Add(budget); return Task.FromResult<RepositoryPayload?>(null); }, (_, _) => Task.FromResult(true),
            budget: TimeSpan.FromSeconds(3));

        await Assert.That(budgets).Count().IsEqualTo(1);
        await Assert.That(budgets[0]).IsLessThanOrEqualTo(TimeSpan.FromSeconds(3));
        await Assert.That(budgets[0]).IsGreaterThan(TimeSpan.Zero);
    }

    [Test]
    public async Task An_exhausted_budget_probes_nothing() {
        var state  = StateWithSecondaryRoot();
        var probed = 0;

        await Link(state, (_, _) => { probed++; return Task.FromResult<RepositoryPayload?>(CliPr()); }, (_, _) => Task.FromResult(true),
            budget: TimeSpan.Zero);

        await Assert.That(probed).IsEqualTo(0);
    }

    [Test]
    public async Task A_cancelled_pass_probes_nothing() {
        var state  = StateWithSecondaryRoot();
        var probed = 0;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Link(state, (_, _) => { probed++; return Task.FromResult<RepositoryPayload?>(CliPr()); }, (_, _) => Task.FromResult(true), ct: cts.Token);

        await Assert.That(probed).IsEqualTo(0);
    }

    [Test]
    public async Task The_post_sees_the_callers_cancellation() {
        var state = StateWithSecondaryRoot();
        using var cts = new CancellationTokenSource();
        var cancelledDuringPost = false;

        await Link(state, Detects(CliPr()), (_, token) => {
            cts.Cancel();
            cancelledDuringPost = token.IsCancellationRequested;
            return Task.FromResult(true);
        }, ct: cts.Token);

        await Assert.That(cancelledDuringPost).IsTrue();
    }
}
