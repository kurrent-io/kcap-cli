using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.PullRequests;
using DynamicData;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

/// The rail's PR colours come from one cache that reads every listed session's links and
/// overviews on a slow cadence, independent of which workspace is open.
public class PullRequestToneCacheTests {
    static readonly RepoIdentity Repo = new("path:/repo", "repo");

    static AgentRow Row(string id, string? sessionId) => AgentRow.FromLocal(
        new(id, "agent", "claude", "/repo", "Running", null, null, null, DateTime.UtcNow, null, null, SessionId: sessionId), Repo);

    static (FakeAgentDirectory Directory, FakePullRequestSource Source, FakeTimeProvider Time, PullRequestToneCache Cache) Build() {
        var directory = new FakeAgentDirectory();
        var time = new FakeTimeProvider();
        var source = new FakePullRequestSource(time);
        var cache = new PullRequestToneCache(directory, source, time);
        return (directory, source, time, cache);
    }

    [Test]
    public async Task A_listed_session_with_an_open_passing_pr_reads_as_ready() {
        var (directory, source, _, cache) = Build();
        using var _c = cache;
        using var _d = directory;

        directory.Rows.AddOrUpdate(Row("a1", "s1"));

        await WaitUntilAsync(() => cache.Current.ContainsKey("s1"), what: "tone for s1");
        await Assert.That(cache.Current["s1"]).IsEqualTo(PullRequestTone.Ready);
        await Assert.That(source.Lists).IsEqualTo(1);
        await Assert.That(source.Overviews).IsEqualTo(source.Links.Length);
    }

    [Test]
    public async Task A_session_without_links_carries_no_tone() {
        var (directory, source, _, cache) = Build();
        using var _c = cache;
        using var _d = directory;
        source.Links = [];

        directory.Rows.AddOrUpdate(Row("a1", "s1"));

        await WaitUntilAsync(() => source.Lists == 1, what: "one list read");
        await Assert.That(cache.Current.ContainsKey("s1")).IsFalse();
    }

    [Test]
    public async Task A_row_without_a_session_is_never_read() {
        var (directory, source, time, cache) = Build();
        using var _c = cache;
        using var _d = directory;

        directory.Rows.AddOrUpdate(Row("a1", null));
        time.Advance(TimeSpan.FromMinutes(5));

        await Assert.That(source.Lists).IsEqualTo(0);
    }

    [Test]
    public async Task A_removed_row_drops_its_tone() {
        var (directory, _, _, cache) = Build();
        using var _c = cache;
        using var _d = directory;
        directory.Rows.AddOrUpdate(Row("a1", "s1"));
        await WaitUntilAsync(() => cache.Current.ContainsKey("s1"), what: "tone for s1");

        directory.Rows.Remove(Row("a1", "s1").Key);

        await WaitUntilAsync(() => !cache.Current.ContainsKey("s1"), what: "tone dropped");
    }

    [Test]
    public async Task A_session_is_reread_only_after_the_refresh_interval() {
        var (directory, source, time, cache) = Build();
        using var _c = cache;
        using var _d = directory;
        directory.Rows.AddOrUpdate(Row("a1", "s1"));
        await WaitUntilAsync(() => source.Lists == 1, what: "first read");

        time.Advance(TimeSpan.FromSeconds(45));
        await Assert.That(source.Lists).IsEqualTo(1);

        time.Advance(TimeSpan.FromMinutes(2));
        await WaitUntilAsync(() => source.Lists == 2, what: "second read");
    }

    [Test]
    public async Task A_denied_overview_drops_the_tone() {
        var (directory, source, time, cache) = Build();
        using var _c = cache;
        using var _d = directory;
        directory.Rows.AddOrUpdate(Row("a1", "s1"));
        await WaitUntilAsync(() => cache.Current.ContainsKey("s1"), what: "tone for s1");

        source.Failure = "denied";
        time.Advance(TimeSpan.FromMinutes(3));

        await WaitUntilAsync(() => !cache.Current.ContainsKey("s1"), what: "tone dropped");
    }

    [Test]
    public async Task The_strongest_tone_across_a_sessions_links_wins() {
        var (directory, source, time, cache) = Build();
        using var _c = cache;
        using var _d = directory;
        source.OverviewResponses.Enqueue((subject, _) => Task.FromResult(source.Overview(subject)));
        source.OverviewResponses.Enqueue((subject, _) => Task.FromResult(source.Overview(subject) with {
            Data = new PullRequestOverviewDto { Lifecycle = "open", Checks = new() { Availability = new() { Status = "ready" }, Rollup = "failure" } } }));

        directory.Rows.AddOrUpdate(Row("a1", "s1"));

        await WaitUntilAsync(() => cache.Current.ContainsKey("s1"), what: "tone for s1");
        await Assert.That(cache.Current["s1"]).IsEqualTo(PullRequestTone.ChecksFailed);
    }

    static Task WaitUntilAsync(Func<bool> condition, string what) => WorkspaceFixtures.WaitUntilAsync(condition, what: what);
}
