using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.PullRequests;
using Capacitor.Remote.Models;
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

    [Test]
    public async Task A_denied_overview_beats_a_readable_one() {
        var (directory, source, _, cache) = Build();
        using var _c = cache;
        using var _d = directory;
        source.OverviewResponses.Enqueue((subject, _) => Task.FromResult(source.Overview(subject)));
        source.OverviewResponses.Enqueue((subject, _) => Task.FromResult(new PullRequestRead<PullRequestOverviewDto>(
            PullRequestReadKind.Unavailable, Subject: subject, AccessFailure: "denied", Reason: "github_access_denied")));

        directory.Rows.AddOrUpdate(Row("a1", "s1"));

        await WaitUntilAsync(() => source.Overviews == 2, what: "both overviews read");
        await Assert.That(cache.Current.ContainsKey("s1")).IsFalse();
    }

    [Test]
    public async Task Signing_out_clears_the_tones() {
        var (directory, source, time, cache) = Build();
        using var _c = cache;
        using var _d = directory;
        directory.Rows.AddOrUpdate(Row("a1", "s1"));
        await WaitUntilAsync(() => cache.Current.ContainsKey("s1"), what: "tone for s1");

        source.Capability = PullRequestCapabilityKind.SignedOut;
        time.Advance(TimeSpan.FromMinutes(3));

        await WaitUntilAsync(() => !cache.Current.ContainsKey("s1"), what: "tone dropped");
    }

    /// A transient discovery miss is not a verdict: the last tone stands until a read says otherwise.
    [Test]
    public async Task A_transient_discovery_miss_keeps_the_tone() {
        var (directory, source, time, cache) = Build();
        using var _c = cache;
        using var _d = directory;
        directory.Rows.AddOrUpdate(Row("a1", "s1"));
        await WaitUntilAsync(() => cache.Current.ContainsKey("s1"), what: "tone for s1");

        source.Capability = PullRequestCapabilityKind.Unavailable;
        time.Advance(TimeSpan.FromMinutes(3));

        await Assert.That(cache.Current["s1"]).IsEqualTo(PullRequestTone.Ready);
    }

    /// While the local daemon reports another server, its session ids name that server's sessions;
    /// reading them against the app's server would colour a worktree from a colliding session.
    [Test]
    public async Task Local_rows_are_skipped_while_the_daemon_reports_another_server() {
        var (directory, source, _, cache) = Build();
        using var _c = cache;
        using var _d = directory;
        directory.LocalOnAppServer.OnNext(false);

        directory.Rows.AddOrUpdate(Row("a1", "s1"));
        directory.Rows.AddOrUpdate(AgentRow.FromRemote(new AgentInstanceDto {
            AgentId = "b1", Status = "Running", DaemonName = "work-mac", OwnerUserId = "u1",
            Vendor = "claude", RepoOwner = "o", RepoName = "r", SessionId = "s2",
        }));

        await WaitUntilAsync(() => cache.Current.ContainsKey("s2"), what: "tone for the remote session");
        await Assert.That(cache.Current.ContainsKey("s1")).IsFalse();
        await Assert.That(source.Lists).IsEqualTo(1);
    }

    /// A read whose access window has lapsed is not revealed anywhere else, so it yields no tone either.
    [Test]
    public async Task An_overview_past_its_access_window_gives_no_tone() {
        var (directory, source, _, cache) = Build();
        using var _c = cache;
        using var _d = directory;
        foreach (var _ in source.Links)
            source.OverviewResponses.Enqueue((subject, _) => Task.FromResult(source.Overview(subject) with { AccessValidForSeconds = 0 }));

        directory.Rows.AddOrUpdate(Row("a1", "s1"));

        await WaitUntilAsync(() => source.Overviews == source.Links.Length, what: "overviews read");
        await Assert.That(cache.Current.ContainsKey("s1")).IsFalse();
    }

    /// The readable remainder of a partly-missed refresh could only understate the session, so
    /// the whole last tone stands until every PR reads again.
    [Test]
    public async Task A_partial_transient_miss_keeps_the_last_tone_whole() {
        var (directory, source, time, cache) = Build();
        using var _c = cache;
        using var _d = directory;
        PullRequestRead<PullRequestOverviewDto> Failing(PullRequestSubjectDto subject) => source.Overview(subject) with {
            Data = new PullRequestOverviewDto { Lifecycle = "open", Checks = new() { Availability = new() { Status = "ready" }, Rollup = "failure" } } };
        source.OverviewResponses.Enqueue((subject, _) => Task.FromResult(Failing(subject)));
        source.OverviewResponses.Enqueue((subject, _) => Task.FromResult(source.Overview(subject)));
        directory.Rows.AddOrUpdate(Row("a1", "s1"));
        await WaitUntilAsync(() => cache.Current.GetValueOrDefault("s1") == PullRequestTone.ChecksFailed, what: "failed tone");

        source.OverviewResponses.Enqueue((subject, _) => Task.FromResult(new PullRequestRead<PullRequestOverviewDto>(
            PullRequestReadKind.Unavailable, Subject: subject, AccessFailure: "transient", Reason: "timeout")));
        source.OverviewResponses.Enqueue((subject, _) => Task.FromResult(source.Overview(subject)));
        time.Advance(TimeSpan.FromMinutes(3));

        await WaitUntilAsync(() => source.Overviews == 4, what: "second refresh");
        await Assert.That(cache.Current["s1"]).IsEqualTo(PullRequestTone.ChecksFailed);
    }

    /// A session that leaves and returns is a new listing, read at once rather than on the old
    /// session's refresh clock.
    [Test]
    public async Task A_returning_session_is_read_again_at_once() {
        var (directory, source, _, cache) = Build();
        using var _c = cache;
        using var _d = directory;
        source.Links = [];
        directory.Rows.AddOrUpdate(Row("a1", "s1"));
        await WaitUntilAsync(() => source.Lists == 1, what: "first read");

        directory.Rows.Remove(Row("a1", "s1").Key);
        directory.Rows.AddOrUpdate(Row("a1", "s1"));

        await WaitUntilAsync(() => source.Lists == 2, what: "read on return");
    }

    static Task WaitUntilAsync(Func<bool> condition, string what) => WorkspaceFixtures.WaitUntilAsync(condition, what: what);
}
