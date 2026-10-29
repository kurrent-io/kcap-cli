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
    [Test]
    public async Task A_workspace_refresh_discovers_attached_PRs_without_refreshing_other_sessions() {
        var (directory, source, _, cache) = Build();
        using var _c = cache;
        using var _d = directory;
        source.Links = [];
        directory.Rows.AddOrUpdate(Row("a1", "s1"));
        directory.Rows.AddOrUpdate(Row("a2", "s2"));
        await WaitUntilAsync(() => source.Lists == 2, "initial empty lists");

        source.Links = [FakePullRequestSource.Link(142)];
        cache.Refresh("s1");

        await WaitUntilAsync(() => cache.CurrentSummaries.ContainsKey("s1"), "attached PR in rail");
        await Assert.That(cache.CurrentSummaries["s1"].Subject!.Number).IsEqualTo(142);
        await Assert.That(cache.CurrentSummaries.ContainsKey("s2")).IsFalse();
        cache.Refresh("unlisted");
        await Assert.That(source.Lists).IsEqualTo(3);

        source.Links = [];
        cache.Refresh("s1");
        await WaitUntilAsync(() => !cache.CurrentSummaries.ContainsKey("s1"), "detached PR removed");
        await Assert.That(cache.Current.ContainsKey("s1")).IsFalse();
    }

    [Test]
    public async Task A_workspace_refresh_during_a_read_rejects_old_results_and_queues_one_follow_up() {
        var (directory, source, _, cache) = Build();
        using var _c = cache;
        using var _d = directory;
        var oldRead = new TaskCompletionSource<PullRequestRead<PullRequestOverviewDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newRead = new TaskCompletionSource<PullRequestRead<PullRequestOverviewDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Links = [FakePullRequestSource.Link(1)];
        source.OverviewResponses.Enqueue((_, ct) => oldRead.Task.WaitAsync(ct));
        source.OverviewResponses.Enqueue((_, ct) => newRead.Task.WaitAsync(ct));
        directory.Rows.AddOrUpdate(Row("a1", "s1"));
        await WaitUntilAsync(() => source.Overviews == 1, "old read started");

        source.Links = [FakePullRequestSource.Link(2)];
        cache.Refresh("s1");
        cache.Refresh("s1");
        await Assert.That(source.Lists).IsEqualTo(1);
        oldRead.SetResult(source.Overview(PullRequestWire.Subject(FakePullRequestSource.Link(1))));

        await WaitUntilAsync(() => source.Overviews == 2, "follow-up read started");
        await Assert.That(cache.CurrentSummaries.ContainsKey("s1")).IsFalse();
        await Assert.That(cache.Current.ContainsKey("s1")).IsFalse();
        newRead.SetResult(source.Overview(PullRequestWire.Subject(FakePullRequestSource.Link(2))));
        await WaitUntilAsync(() => cache.CurrentSummaries.ContainsKey("s1"), "new PR published");
        await Assert.That(cache.CurrentSummaries["s1"].Subject!.Number).IsEqualTo(2);
        await Assert.That(source.Lists).IsEqualTo(2);
    }

    /// Renewing the window would re-read every shown session every twenty seconds; past it the
    /// chip keeps each PR's number and lifecycle, and the next read waits for the normal cadence.
    [Test]
    public async Task A_lapsed_access_window_drops_titles_without_a_read() {
        var (directory, source, time, cache) = Build();
        using var _c = cache;
        using var _d = directory;
        source.AccessSeconds = 30;
        source.Links = [FakePullRequestSource.Link(142)];
        source.Lifecycles[142] = "draft";
        directory.Rows.AddOrUpdate(Row("a1", "s1"));
        await WaitUntilAsync(() => cache.CurrentSummaries.ContainsKey("s1"), "summary");
        await Assert.That(cache.CurrentSummaries["s1"].MenuItems[0].Title).IsEqualTo("Private PR");

        time.Advance(TimeSpan.FromSeconds(26));
        await WaitUntilAsync(() => cache.CurrentSummaries["s1"].MenuItems[0].Title is null, "titles dropped");
        await Assert.That(cache.CurrentSummaries["s1"].Label).IsEqualTo("PR #142");
        await Assert.That(cache.CurrentSummaries["s1"].Lifecycle).IsEqualTo("Draft");
        await Assert.That(cache.Current["s1"]).IsEqualTo(PullRequestTone.Draft);
        time.Advance(TimeSpan.FromSeconds(60));
        await Task.Delay(50);
        await Assert.That(source.Lists).IsEqualTo(1);

        time.Advance(TimeSpan.FromSeconds(35));
        await WaitUntilAsync(() => cache.CurrentSummaries["s1"].MenuItems[0].Title == "Private PR", "titles back on the next read");
        await Assert.That(source.Lists).IsEqualTo(2);
    }

    [Test]
    public async Task A_transient_failure_keeps_the_untitled_summary() {
        var (directory, source, time, cache) = Build();
        using var _c = cache;
        using var _d = directory;
        source.AccessSeconds = 30;
        directory.Rows.AddOrUpdate(Row("a1", "s1"));
        await WaitUntilAsync(() => cache.CurrentSummaries.ContainsKey("s1"), "summary");
        source.Failure = "transient";
        time.Advance(TimeSpan.FromMinutes(2));
        await WaitUntilAsync(() => source.Lists == 2, "failed read");
        await Assert.That(cache.CurrentSummaries["s1"].Count).IsEqualTo(2);
        await Assert.That(cache.CurrentSummaries["s1"].MenuItems.All(i => i.Title is null)).IsTrue();
    }

    [Test]
    public async Task Rereading_an_unchanged_summary_preserves_its_identity_when_another_session_changes() {
        var (directory, source, time, cache) = Build();
        using var _c = cache;
        using var _d = directory;
        directory.Rows.AddOrUpdate(Row("a1", "s1"));
        directory.Rows.AddOrUpdate(Row("a2", "s2"));
        await WaitUntilAsync(() => cache.CurrentSummaries.Count == 2, "both summaries");
        var summary = cache.CurrentSummaries["s1"];
        time.Advance(TimeSpan.FromMinutes(2));
        await WaitUntilAsync(() => source.Lists == 4, "both summaries read again");
        directory.Rows.Remove("local:a2");
        await WaitUntilAsync(() => cache.CurrentSummaries.Count == 1, "other session removed");
        await Assert.That(ReferenceEquals(cache.CurrentSummaries["s1"], summary)).IsTrue();
        source.Lifecycles[1] = "merged";
        time.Advance(TimeSpan.FromMinutes(2));
        await WaitUntilAsync(() => cache.CurrentSummaries["s1"].MenuItems[0].Lifecycle == "Merged", "real metadata change");
        await Assert.That(ReferenceEquals(cache.CurrentSummaries["s1"], summary)).IsFalse();
    }

    [Test]
    public async Task Summary_uses_lifecycle_for_one_pr_and_count_for_several() {
        var (directory, source, time, cache) = Build();
        using var _c = cache;
        using var _d = directory;
        source.Links = [FakePullRequestSource.Link(142)];
        source.Lifecycles[142] = "draft";
        directory.Rows.AddOrUpdate(Row("a1", "s1"));
        await WaitUntilAsync(() => cache.CurrentSummaries.ContainsKey("s1"), "summary");
        await Assert.That(cache.CurrentSummaries["s1"].Label).IsEqualTo("PR #142");
        await Assert.That(cache.CurrentSummaries["s1"].Status.Text).IsEqualTo("Draft");
        source.Lifecycles[142] = "merged";
        time.Advance(TimeSpan.FromMinutes(2));
        await WaitUntilAsync(() => cache.CurrentSummaries["s1"].Lifecycle == "Merged", "merged summary");
        source.Links = [FakePullRequestSource.Link(142), FakePullRequestSource.Link(143), FakePullRequestSource.Link(143)];
        time.Advance(TimeSpan.FromMinutes(2));
        await WaitUntilAsync(() => cache.CurrentSummaries["s1"].Count == 2, "multiple summary");
        await Assert.That(cache.CurrentSummaries["s1"].Label).IsEqualTo("2 PRs");
        await Assert.That(cache.CurrentSummaries["s1"].MenuItems.Select(i => i.Subject.Number)).IsEquivalentTo([142, 143]);
        await Assert.That(cache.CurrentSummaries["s1"].MenuItems.Single(i => i.Subject.Number == 142).Lifecycle).IsEqualTo("Merged");
    }

    [Test]
    public async Task A_denied_refresh_clears_metadata_immediately() {
        var (directory, source, time, cache) = Build();
        using var _c = cache;
        using var _d = directory;
        directory.Rows.AddOrUpdate(Row("a1", "s1"));
        await WaitUntilAsync(() => cache.CurrentSummaries.ContainsKey("s1"), "summary");
        source.Failure = "denied";
        time.Advance(TimeSpan.FromMinutes(2));
        await WaitUntilAsync(() => !cache.CurrentSummaries.ContainsKey("s1"), "revoked summary");
    }

    /// An ended session whose PRs all merged or closed has nothing left to change on a timer; a
    /// workspace that discovers new links still reads it.
    [Test]
    public async Task An_ended_session_whose_prs_all_settled_is_not_read_again_on_a_timer() {
        var (directory, source, time, cache) = Build();
        using var _c = cache;
        using var _d = directory;
        source.Lifecycles[1] = "merged";
        source.Lifecycles[2] = "closed";
        directory.Rows.AddOrUpdate(Ended("s1"));
        directory.Rows.AddOrUpdate(Ended("s2"));
        await WaitUntilAsync(() => cache.CurrentSummaries.Count == 2, "both read");
        source.Lifecycles[1] = "open";
        await Assert.That(source.Lists).IsEqualTo(2);

        source.Links = [FakePullRequestSource.Link(1)];
        time.Advance(TimeSpan.FromMinutes(11));
        await Task.Delay(50);
        await Assert.That(source.Lists).IsEqualTo(2);

        cache.Refresh("s1");
        await WaitUntilAsync(() => source.Lists == 3, "workspace refresh");
        await WaitUntilAsync(() => cache.CurrentSummaries["s1"].Lifecycle == "Open", "reopened PR");
        time.Advance(TimeSpan.FromMinutes(11));
        await WaitUntilAsync(() => source.Lists == 4, "an open PR is read on the slow cadence again");
    }

    /// Nothing shows a hidden row's PRs, so it is not read until the rail's filter admits it.
    [Test]
    public async Task A_row_the_rail_hides_is_not_read() {
        var directory = new FakeAgentDirectory();
        var time = new FakeTimeProvider();
        var source = new FakePullRequestSource(time) { AccessSeconds = 300 };
        var shown = new System.Reactive.Subjects.BehaviorSubject<Func<AgentRow, bool>>(row => !row.IsEndedSession);
        using var cache = new PullRequestToneCache(directory, source, time, shown);
        using var _d = directory;
        directory.Rows.AddOrUpdate(Row("a1", "live"));
        directory.Rows.AddOrUpdate(Ended("ended"));
        await WaitUntilAsync(() => cache.CurrentSummaries.ContainsKey("live"), "shown row read");
        time.Advance(TimeSpan.FromMinutes(11));
        await WaitUntilAsync(() => source.Lists == 2, "shown row read again");
        await Assert.That(cache.CurrentSummaries.ContainsKey("ended")).IsFalse();

        shown.OnNext(_ => true);
        await WaitUntilAsync(() => cache.CurrentSummaries.ContainsKey("ended"), "row read once shown");
        await Assert.That(source.Lists).IsEqualTo(3);
    }

    /// A settled session whose forced read fails is not left unread for good: the next read goes on its cadence.
    [Test]
    public async Task A_failed_read_of_a_settled_session_returns_it_to_the_slow_cadence() {
        var (directory, source, time, cache) = Build();
        using var _c = cache;
        using var _d = directory;
        source.Lifecycles[1] = "merged";
        source.Lifecycles[2] = "merged";
        directory.Rows.AddOrUpdate(Ended("s1"));
        await WaitUntilAsync(() => cache.CurrentSummaries.ContainsKey("s1"), "settled read");

        source.Failure = "transient";
        cache.Refresh("s1");
        await WaitUntilAsync(() => source.Lists == 2, "failed forced read");
        source.Failure = null;
        time.Advance(TimeSpan.FromMinutes(11));
        await WaitUntilAsync(() => source.Lists == 3, "read again on the slow cadence");
    }

    /// Hiding a row keeps what it showed and when it was read, so showing it again neither blanks
    /// its chip nor reads it ahead of its cadence.
    [Test]
    public async Task A_row_shown_again_keeps_its_chip_and_its_cadence() {
        var directory = new FakeAgentDirectory();
        var time = new FakeTimeProvider();
        var source = new FakePullRequestSource(time) { AccessSeconds = 300 };
        var shown = new System.Reactive.Subjects.BehaviorSubject<Func<AgentRow, bool>>(_ => true);
        using var cache = new PullRequestToneCache(directory, source, time, shown);
        using var _d = directory;
        directory.Rows.AddOrUpdate(Ended("s1"));
        await WaitUntilAsync(() => cache.CurrentSummaries.ContainsKey("s1"), "first read");

        shown.OnNext(row => !row.IsEndedSession);
        time.Advance(TimeSpan.FromMinutes(1));
        shown.OnNext(_ => true);
        await Task.Delay(50);
        await Assert.That(cache.CurrentSummaries.ContainsKey("s1")).IsTrue();
        await Assert.That(source.Lists).IsEqualTo(1);
    }

    static readonly RepoIdentity Repo = new("path:/repo", "repo");

    static AgentRow Row(string id, string? sessionId) => AgentRow.FromLocal(
        new(id, "agent", "claude", "/repo", "Running", null, null, null, DateTime.UtcNow, null, null, SessionId: sessionId), Repo);

    static AgentRow Ended(string sessionId) => ServerRow(sessionId, "ended");

    /// The viewer's own listed session, as the directory marks it once the owners read has landed.
    static AgentRow ServerRow(string sessionId, string status) => AgentRow.FromServer(new Capacitor.Remote.Models.RepoSessionDto {
        SessionId = sessionId, Status = status, AccessLevel = "full", Vendor = "claude", Title = "Session",
        Owner = new() { UserId = "u1", DisplayName = "Alexey" },
        StartedAt = DateTimeOffset.UnixEpoch, LastActivityAt = DateTimeOffset.UnixEpoch.AddHours(1),
        EndedAt = status == "ended" ? DateTimeOffset.UnixEpoch.AddHours(1) : null,
        Repo = new() { Hash = "h", Owner = "kurrent-io", Name = "kcap-cli" },
    }, hostedPresentation: false) with { OwnerIsViewer = true };

    /// A teammate's session is not this app's to poll, whichever lane shows it: under Team the
    /// listing holds hundreds, and each read costs the server a visibility check and, per PR, a
    /// GitHub-backed admission. Its PRs show when the session opens. A listed row whose owner the
    /// server has not named yet waits too; a daemon row it has not named is presumed the viewer's.
    [Test]
    public async Task A_row_the_server_names_as_a_teammates_is_not_read() {
        var (directory, source, _, cache) = Build();
        using var _c = cache;
        using var _d = directory;
        directory.Rows.AddOrUpdate(ServerRow("mine", "active"));
        directory.Rows.AddOrUpdate(ServerRow("theirs", "active") with { OwnerIsViewer = false });
        directory.Rows.AddOrUpdate(ServerRow("unknown", "ended") with { OwnerIsViewer = null });
        directory.Rows.AddOrUpdate(Row("a1", "local-theirs") with { OwnerIsViewer = false });
        directory.Rows.AddOrUpdate(AgentRow.FromRemote(new AgentInstanceDto {
            AgentId = "b1", Status = "Running", DaemonName = "work-mac", OwnerUserId = "u2",
            Vendor = "claude", RepoOwner = "o", RepoName = "r", SessionId = "remote-theirs",
        }) with { OwnerIsViewer = false });

        await WaitUntilAsync(() => cache.CurrentSummaries.ContainsKey("mine"), "own row read");
        await Task.Delay(100);
        await Assert.That(source.Lists).IsEqualTo(1);
        await Assert.That(cache.CurrentSummaries.Keys).IsEquivalentTo(new[] { "mine" });

        directory.Rows.AddOrUpdate(ServerRow("unknown", "ended"));

        await WaitUntilAsync(() => cache.CurrentSummaries.ContainsKey("unknown"), "row read once its owner is known");
        await Assert.That(source.Lists).IsEqualTo(2);
    }

    static RepoSessionPullRequestDto ListedPr(int number, string? state) => new() {
        RepoHash = "h", Owner = "Kurrent-IO", RepoName = "kcap-cli", Number = number,
        Url = $"https://github.com/kurrent-io/kcap-cli/pull/{number}", Title = $"PR {number}", State = state };

    /// A row whose listing carried its PRs is marked from the row, a teammate's included, and the
    /// PR routes are not read for it at all.
    [Test]
    public async Task A_listed_row_is_marked_from_its_listing_without_a_read() {
        var (directory, source, time, cache) = Build();
        using var _c = cache;
        using var _d = directory;
        directory.Rows.AddOrUpdate(ServerRow("theirs", "active") with {
            OwnerIsViewer = false, ListedPullRequests = new([ListedPr(7, "draft"), ListedPr(8, "merged")]) });
        directory.Rows.AddOrUpdate(Row("a1", "mine") with { ListedPullRequests = new([ListedPr(9, null)]) });

        await WaitUntilAsync(() => cache.CurrentSummaries.Count == 2, "both rows marked");
        var theirs = cache.CurrentSummaries["theirs"];
        await Assert.That(theirs.MenuItems.Select(i => i.Lifecycle)).IsEquivalentTo(new[] { "Draft", "Merged" });
        await Assert.That(theirs.MenuItems[0].Subject).IsEqualTo(new PullRequestSubjectDto {
            Provider = "github", Host = "github.com", RepoHash = "h", Owner = "kurrent-io", RepoName = "kcap-cli", Number = 7 });
        await Assert.That(cache.Current["theirs"]).IsEqualTo(PullRequestTone.Draft);
        // No lifecycle recorded, as on a tenant without enrichment: a neutral mark, no tone.
        await Assert.That(cache.CurrentSummaries["mine"].Status.Text).IsEqualTo("Unknown");
        await Assert.That(cache.Current.ContainsKey("mine")).IsFalse();

        time.Advance(TimeSpan.FromMinutes(15));
        await Task.Delay(50);
        await Assert.That(source.Lists).IsEqualTo(0);
        await Assert.That(source.Overviews).IsEqualTo(0);
    }

    /// Withheld below full access, or listed with none: no mark, and no fall back to the PR routes.
    [Test]
    public async Task A_listed_row_without_visible_links_shows_no_mark_and_is_not_read() {
        var (directory, source, time, cache) = Build();
        using var _c = cache;
        using var _d = directory;
        directory.Rows.AddOrUpdate(ServerRow("withheld", "active") with { ListedPullRequests = RepoSessionPullRequests.Withheld });
        directory.Rows.AddOrUpdate(ServerRow("none", "ended") with { ListedPullRequests = new([]) });
        directory.Rows.AddOrUpdate(ServerRow("older", "ended"));

        await WaitUntilAsync(() => cache.CurrentSummaries.ContainsKey("older"), "row without the field read as before");
        time.Advance(TimeSpan.FromMinutes(15));
        await WaitUntilAsync(() => source.Lists == 2, "only the unlisted row read again");
        await Assert.That(cache.CurrentSummaries.Keys).IsEquivalentTo(new[] { "older" });
    }

    /// A polled row whose listing starts carrying its PRs drops the read's mark for the listing's,
    /// and a later change to the listing shows without a read.
    [Test]
    public async Task A_row_that_becomes_listed_takes_the_listings_mark() {
        var (directory, source, _, cache) = Build();
        using var _c = cache;
        using var _d = directory;
        directory.Rows.AddOrUpdate(ServerRow("s1", "active"));
        await WaitUntilAsync(() => cache.CurrentSummaries.ContainsKey("s1"), "read summary");

        directory.Rows.AddOrUpdate(ServerRow("s1", "active") with { ListedPullRequests = new([ListedPr(7, "open")]) });
        await WaitUntilAsync(() => cache.CurrentSummaries["s1"].Subject?.Number == 7, "listing summary");
        directory.Rows.AddOrUpdate(ServerRow("s1", "active") with { ListedPullRequests = new([ListedPr(7, "closed")]) });
        await WaitUntilAsync(() => cache.CurrentSummaries["s1"].Lifecycle == "Closed", "listing change");
        await Assert.That(cache.Current["s1"]).IsEqualTo(PullRequestTone.Closed);
        await Assert.That(source.Lists).IsEqualTo(1);
    }

    static (FakeAgentDirectory Directory, FakePullRequestSource Source, FakeTimeProvider Time, PullRequestToneCache Cache) Build() {
        var directory = new FakeAgentDirectory();
        var time = new FakeTimeProvider();
        var source = new FakePullRequestSource(time) { AccessSeconds = 300 };
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

    /// An ended session's pull requests move rarely, so its rows read on the slow cadence while a
    /// live row keeps the two-minute one.
    [Test]
    public async Task An_ended_sessions_pull_requests_refresh_on_the_slow_cadence() {
        var (directory, source, time, cache) = Build();
        using var _c = cache;
        using var _d = directory;
        source.AccessSeconds = 3600;
        directory.Rows.AddOrUpdate(Ended("s1"));
        await WaitUntilAsync(() => source.Lists == 1, what: "first read");

        time.Advance(TimeSpan.FromMinutes(2).Add(TimeSpan.FromSeconds(1)));
        await Assert.That(source.Lists).IsEqualTo(1);

        time.Advance(TimeSpan.FromMinutes(8));
        await WaitUntilAsync(() => source.Lists == 2, what: "second read");
    }

    /// A session no daemon hosts is somebody's terminal or a teammate's: under Team there can be
    /// hundreds, and none of them is this app's to poll every two minutes.
    [Test]
    public async Task A_live_server_rows_pull_requests_refresh_on_the_slow_cadence() {
        var (directory, source, time, cache) = Build();
        using var _c = cache;
        using var _d = directory;
        source.AccessSeconds = 3600;
        directory.Rows.AddOrUpdate(ServerRow("s1", "active"));
        await WaitUntilAsync(() => source.Lists == 1, what: "first read");

        time.Advance(TimeSpan.FromMinutes(2).Add(TimeSpan.FromSeconds(1)));
        await Assert.That(source.Lists).IsEqualTo(1);

        time.Advance(TimeSpan.FromMinutes(8));
        await WaitUntilAsync(() => source.Lists == 2, what: "second read");
    }

    /// A daemon taking the session up puts it back on the fast cadence at once, not after the slow
    /// interval it was last given.
    [Test]
    public async Task A_session_a_daemon_takes_up_returns_to_the_fast_cadence() {
        var (directory, source, time, cache) = Build();
        using var _c = cache;
        using var _d = directory;
        source.AccessSeconds = 3600;
        directory.Rows.AddOrUpdate(ServerRow("s1", "active"));
        await WaitUntilAsync(() => source.Lists == 1, what: "first read");

        directory.Rows.AddOrUpdate(Row("a1", "s1"));
        time.Advance(TimeSpan.FromMinutes(2).Add(TimeSpan.FromSeconds(1)));

        await WaitUntilAsync(() => source.Lists == 2, what: "read on the fast cadence");
    }

    /// Every listed session comes due at once after a connect or a scope switch; the reads go out no
    /// more at once than the server admits from one user.
    [Test]
    public async Task A_tick_starts_at_most_four_reads_at_once() {
        var (directory, source, _, cache) = Build();
        using var _c = cache;
        using var _d = directory;
        source.ListGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        directory.Rows.Edit(rows => { for (var i = 0; i < 20; i++) rows.AddOrUpdate(Ended($"s{i}")); });

        await WaitUntilAsync(() => source.Lists >= 4, what: "the first reads");
        await Task.Delay(100);
        await Assert.That(source.Lists).IsEqualTo(4);

        source.ListGate.SetResult();
        await WaitUntilAsync(() => source.Lists == 20, what: "the rest follow");
    }

    /// A refused read holds every read until the server's retry time, then goes again at once
    /// rather than on its session's two-minute cadence; the tone it had stands meanwhile.
    [Test]
    [Arguments("links")]
    [Arguments("overview")]
    public async Task A_refused_read_holds_all_reads_until_the_servers_retry_time(string route) {
        var (directory, source, time, cache) = Build();
        using var _c = cache;
        using var _d = directory;
        source.AccessSeconds = 3600;
        directory.Rows.AddOrUpdate(Row("a1", "s1"));
        await WaitUntilAsync(() => cache.Current.ContainsKey("s1"), what: "tone for s1");

        var refusedAt = time.GetUtcNow().UtcDateTime.AddMinutes(2);
        if (route == "links")
            source.ListResponses.Enqueue(new(PullRequestReadKind.Unavailable, Reason: "server_unavailable", AccessFailure: "transient",
                RetryAt: refusedAt.AddSeconds(30), StatusCode: 429));
        else
            source.OverviewResponses.Enqueue((subject, _) => Task.FromResult(new PullRequestRead<PullRequestOverviewDto>(PullRequestReadKind.Unavailable,
                Subject: subject, Reason: "server_unavailable", AccessFailure: "transient", RetryAt: refusedAt.AddSeconds(30), StatusCode: 429)));
        time.Advance(TimeSpan.FromMinutes(2));
        await WaitUntilAsync(() => source.Lists == 2, what: "the refused read");
        directory.Rows.AddOrUpdate(Row("a2", "s2"));
        time.Advance(TimeSpan.FromSeconds(29));
        await Task.Delay(50);
        await Assert.That(source.Lists).IsEqualTo(2);
        await Assert.That(cache.Current["s1"]).IsEqualTo(PullRequestTone.Ready);

        time.Advance(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => source.Lists == 4, what: "both read once the hold lapses");
    }

    static PullRequestRead<PullRequestOverviewDto> RateLimited(PullRequestSubjectDto subject, DateTime retryAt) => new(
        PullRequestReadKind.Unavailable, Subject: subject, Reason: "server_unavailable", AccessFailure: "transient", RetryAt: retryAt, StatusCode: 429);

    /// The budget is the user's: a read already in flight when another session is refused sends no
    /// further request before the retry time.
    [Test]
    public async Task A_read_in_flight_sends_nothing_more_once_another_is_refused() {
        var (directory, source, time, cache) = Build();
        using var _c = cache;
        using var _d = directory;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.OverviewResponses.Enqueue(async (subject, ct) => { await gate.Task.WaitAsync(ct); return source.Overview(subject); });
        source.OverviewResponses.Enqueue((subject, _) => Task.FromResult(RateLimited(subject, time.GetUtcNow().UtcDateTime.AddSeconds(30))));
        directory.Rows.AddOrUpdate(Row("a1", "s1"));
        directory.Rows.AddOrUpdate(Row("a2", "s2"));
        await WaitUntilAsync(() => source.Overviews == 2, what: "one read held open, the other refused");

        gate.SetResult();
        await Task.Delay(100);
        await Assert.That(source.Overviews).IsEqualTo(2);

        time.Advance(TimeSpan.FromSeconds(31));
        await WaitUntilAsync(() => cache.Current.Count == 2, what: "both read after the hold");
    }

    /// A zero Retry-After still waits a second; the refused read would otherwise go again at once.
    [Test]
    public async Task A_zero_retry_time_still_holds_for_a_second() {
        var (directory, source, time, cache) = Build();
        using var _c = cache;
        using var _d = directory;
        source.AccessSeconds = 3600;
        directory.Rows.AddOrUpdate(Row("a1", "s1"));
        await WaitUntilAsync(() => cache.Current.ContainsKey("s1"), what: "tone for s1");

        // Refused with a retry time of the moment it is answered.
        source.ListResponses.Enqueue(new(PullRequestReadKind.Unavailable, Reason: "server_unavailable", AccessFailure: "transient",
            RetryAt: time.GetUtcNow().UtcDateTime.AddMinutes(2), StatusCode: 429));
        time.Advance(TimeSpan.FromMinutes(2));
        await WaitUntilAsync(() => source.Lists == 2, what: "the refused read");
        await Task.Delay(100);
        await Assert.That(source.Lists).IsEqualTo(2);

        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntilAsync(() => source.Lists == 3, what: "the read after the floor");
    }

    /// A denial read before a refusal still clears the tone: the refusal only hides the PRs after it.
    [Test]
    public async Task A_denial_before_a_refusal_still_drops_the_tone() {
        var (directory, source, time, cache) = Build();
        using var _c = cache;
        using var _d = directory;
        directory.Rows.AddOrUpdate(Row("a1", "s1"));
        await WaitUntilAsync(() => cache.Current.ContainsKey("s1"), what: "tone for s1");

        source.OverviewResponses.Enqueue((subject, _) => Task.FromResult(new PullRequestRead<PullRequestOverviewDto>(
            PullRequestReadKind.Unavailable, Subject: subject, AccessFailure: "denied", Reason: "github_access_denied")));
        source.OverviewResponses.Enqueue((subject, _) => Task.FromResult(RateLimited(subject, time.GetUtcNow().UtcDateTime.AddMinutes(5))));
        time.Advance(TimeSpan.FromMinutes(3));

        await WaitUntilAsync(() => !cache.Current.ContainsKey("s1"), what: "tone dropped");
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

    /// A read still in flight when its session leaves the list and returns belongs to the old listing:
    /// its result is dropped, and the returning session's own read starts only once it ends.
    [Test]
    public async Task A_read_from_before_a_session_left_and_returned_publishes_nothing() {
        var (directory, source, _, cache) = Build();
        using var _c = cache;
        using var _d = directory;
        var oldRead = new TaskCompletionSource<PullRequestRead<PullRequestOverviewDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Links = [FakePullRequestSource.Link(1)];
        source.OverviewResponses.Enqueue((_, ct) => oldRead.Task.WaitAsync(ct));
        directory.Rows.AddOrUpdate(Row("a1", "s1"));
        await WaitUntilAsync(() => source.Overviews == 1, "old read started");

        directory.Rows.Remove(Row("a1", "s1").Key);
        directory.Rows.AddOrUpdate(Row("a1", "s1"));
        await Assert.That(source.Lists).IsEqualTo(1);

        source.Links = [FakePullRequestSource.Link(2)];
        oldRead.SetResult(source.Overview(PullRequestWire.Subject(FakePullRequestSource.Link(1))));
        await WaitUntilAsync(() => cache.CurrentSummaries.ContainsKey("s1"), "the returning session's own read");
        await Assert.That(cache.CurrentSummaries["s1"].Subject!.Number).IsEqualTo(2);
        await Assert.That(source.Lists).IsEqualTo(2);
    }

    static Task WaitUntilAsync(Func<bool> condition, string what) => WorkspaceFixtures.WaitUntilAsync(condition, what: what);
}
