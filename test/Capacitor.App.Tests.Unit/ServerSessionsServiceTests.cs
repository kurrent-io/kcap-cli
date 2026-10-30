using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.Remote.Models;
using DynamicData;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

public class ServerSessionsServiceTests {
    /// Answers each (state, owner, offset) from a script and records every query; a gate holds a
    /// matching query open until the test releases it.
    sealed class ScriptedListing {
        readonly Lock _lock = new();
        readonly Dictionary<(string State, string? Owner, int Offset), ServerSessionsFetch> _answers = new();
        readonly List<ServerSessionsQuery> _calls = [];
        public Func<ServerSessionsQuery, Task>? Gate;

        public IReadOnlyList<ServerSessionsQuery> Calls { get { lock (_lock) return [.. _calls]; } }

        public void Answer(string state, string? owner, int offset, int total, params RepoSessionDto[] rows) {
            lock (_lock) _answers[(state, owner, offset)] = new(rows, total);
        }

        public void Fail(string state, string? owner, int offset) {
            lock (_lock) _answers[(state, owner, offset)] = new(null, 0);
        }

        public void Refuse(string state, string? owner, int offset, TimeSpan retryAfter) {
            lock (_lock) _answers[(state, owner, offset)] = new(null, 0, RetryAfter: retryAfter);
        }

        public async Task<ServerSessionsFetch> Fetch(ServerSessionsQuery query, CancellationToken ct) {
            lock (_lock) _calls.Add(query);
            if (Gate is { } gate) await gate(query);
            lock (_lock) {
                return _answers.TryGetValue((query.State, query.Owner, query.Offset), out var answer) ? answer : new(null, 0);
            }
        }

        public int Count(string state, string? owner, int offset) =>
            Calls.Count(q => q.State == state && q.Owner == owner && q.Offset == offset);
    }

    static RepoSessionDto Row(string id, string status = "ended", string owner = "me", DateTimeOffset? lastActivity = null) => new() {
        SessionId = id, Status = status, AccessLevel = "full", Vendor = "claude", Title = id,
        Owner = new() { UserId = owner, DisplayName = owner },
        StartedAt = DateTimeOffset.UnixEpoch, LastActivityAt = lastActivity ?? DateTimeOffset.UnixEpoch,
        EndedAt = status == "ended" ? DateTimeOffset.UnixEpoch : null,
    };

    static RepoSessionDto[] Rows(string prefix, int from, int count, string status = "ended", string owner = "me") =>
        [.. Enumerable.Range(from, count).Select(i => Row($"{prefix}{i}", status, owner))];

    static SessionOwnerOptionDto[] Owners(params string[] ids) =>
        [new() { UserId = ids[0], IsMe = true, Ids = ids }, new() { UserId = "other", IsMe = false, Ids = ["other"] }];

    static async Task Eventually(Func<bool> condition, string what, int ms = 5000) {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    static void Connect(FakeServerLane lane, string subject = "u1", int epoch = 1) =>
        lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: subject, Epoch: epoch));

    static (FakeServerLane Lane, ScriptedListing Listing, FakeTimeProvider Time, ServerSessionsService Service) Build(
            Func<CancellationToken, Task<SessionOwnerOptionDto[]?>>? owners = null, TimeSpan? reconcileEvery = null,
            TimeSpan? pushDebounce = null, IScheduler? pushScheduler = null) {
        var lane = new FakeServerLane();
        var listing = new ScriptedListing();
        var time = new FakeTimeProvider();
        var service = new ServerSessionsService(
            lane, listing.Fetch, owners ?? (_ => Task.FromResult<SessionOwnerOptionDto[]?>(Owners("me"))), time,
            pushDebounce: pushDebounce ?? TimeSpan.Zero, reconcileEvery: reconcileEvery, pushScheduler: pushScheduler);
        return (lane, listing, time, service);
    }

    /// The default script: one active session of mine, one of a teammate, 212 ended of mine in pages of twenty.
    static void ScriptDefault(ScriptedListing listing, int endedTotal = 212, int pagesScripted = 3) {
        listing.Answer("active", "me", 0, 1, Row("live-me", "active"));
        listing.Answer("active", null, 0, 2, Row("live-me", "active"), Row("live-other", "active", "other"));
        for (var page = 0; page < pagesScripted; page++)
            listing.Answer("ended", "me", page * 20, endedTotal, Rows("s", page * 20 + 1, 20));
    }

    [Test]
    public async Task Connected_reads_live_and_first_ended_page_for_mine() {
        var (lane, listing, _, service) = Build();
        using var _ = service;
        ScriptDefault(listing);
        ServerSessionsPaging? paging = null;
        using var sub = service.Paging.Subscribe(p => paging = p);

        Connect(lane);

        await Eventually(() => service.Ended.Count == 20, "first ended page");
        await Assert.That(listing.Count("active", "me", 0)).IsEqualTo(1);
        await Assert.That(listing.Count("active", null, 0)).IsEqualTo(1);
        await Assert.That(listing.Count("ended", "me", 0)).IsEqualTo(1);
        await Assert.That(service.MineLive.Count).IsEqualTo(1);
        await Assert.That(service.Live.Count).IsEqualTo(2);
        await Eventually(() => paging is { Loaded: 20 }, "paging published");
        await Assert.That(paging).IsEqualTo(new ServerSessionsPaging(20, 212, 1, true, false));
    }

    [Test]
    public async Task Show_more_appends_the_next_page_and_reconcile_rereads_the_whole_prefix() {
        var (lane, listing, _, service) = Build();
        using var _ = service;
        ScriptDefault(listing);
        Connect(lane);
        await Eventually(() => service.Ended.Count == 20, "first page");

        await service.ShowMoreAsync();
        await Eventually(() => service.Ended.Count == 40, "second page");
        await Assert.That(listing.Count("ended", "me", 20)).IsEqualTo(1);

        // The next refresh no longer returns s40: it must leave the set, not linger from the old page.
        listing.Answer("ended", "me", 20, 212, Rows("s", 21, 19));
        lane.SessionPushesSubject.OnNext(new SessionPush(SessionPushKind.ActiveRemoved, "live-me"));
        await Eventually(() => listing.Count("ended", "me", 20) == 2, "prefix re-read");
        await Eventually(() => service.Ended.Count == 39, "replaced, not merged");
        await Assert.That(service.Ended.Lookup("s40").HasValue).IsFalse();
        // Connect, Show more and the push each read the whole prefix from its first page.
        await Assert.That(listing.Count("ended", "me", 0)).IsEqualTo(3);
    }

    [Test]
    public async Task A_burst_between_reconciliations_leaves_no_hole() {
        var (lane, listing, _, service) = Build();
        using var _ = service;
        ScriptDefault(listing);
        Connect(lane);
        await Eventually(() => service.Ended.Count == 20, "first page");
        await service.ShowMoreAsync();
        await Eventually(() => service.Ended.Count == 40, "two pages");

        // Thirty sessions ended since: the newest forty are now n1..n30 then s1..s10.
        var fresh = Rows("n", 1, 30).Concat(Rows("s", 1, 20)).ToArray();
        listing.Answer("ended", "me", 0, 242, fresh[..20]);
        listing.Answer("ended", "me", 20, 242, fresh[20..40]);
        await service.ReconcileAsync();

        await Assert.That(service.Ended.Count).IsEqualTo(40);
        await Assert.That(service.Ended.Keys.OrderBy(k => k, StringComparer.Ordinal))
            .IsEquivalentTo(fresh[..40].Select(r => r.SessionId).OrderBy(k => k, StringComparer.Ordinal).ToList());
    }

    /// One failed page keeps what the reconcile read before it, and does not take the rows of the
    /// pages it never reached off the rail.
    [Test]
    public async Task A_reconcile_that_fails_a_page_keeps_the_pages_it_read() {
        var (lane, listing, _, service) = Build();
        using var _ = service;
        ScriptDefault(listing);
        Connect(lane);
        await Eventually(() => service.Ended.Count == 20, "first page");
        await service.ShowMoreAsync();
        await Eventually(() => service.Ended.Count == 40, "second page");

        listing.Answer("ended", "me", 0, 213, [Row("n1"), .. Rows("s", 1, 19)]);
        listing.Fail("ended", "me", 20);
        await service.ReconcileAsync();

        await Assert.That(service.Ended.Lookup("n1").HasValue).IsTrue();
        await Assert.That(Enumerable.Range(21, 20).All(i => service.Ended.Lookup($"s{i}").HasValue)).IsTrue();
    }

    /// The server refuses a read past the user's budget and says when to come back: nothing is
    /// re-requested before then, and the requests in between fold into one read after.
    [Test]
    public async Task A_refused_read_holds_every_read_until_the_servers_retry_time() {
        var (lane, listing, time, service) = Build();
        using var _ = service;
        ScriptDefault(listing);
        // Yields, so a read that re-requests without holding spins off the test's thread.
        listing.Gate = async _ => await Task.Yield();
        listing.Refuse("ended", "me", 0, TimeSpan.FromSeconds(30));
        Connect(lane);
        await Eventually(() => listing.Count("ended", "me", 0) == 1, "refused page");
        await Eventually(() => service.Live.Count == 2, "live rows kept from the refused reconcile");

        var calls = listing.Calls.Count;
        lane.SessionPushesSubject.OnNext(new SessionPush(SessionPushKind.ActiveChanged, "live-me"));
        lane.SessionPushesSubject.OnNext(new SessionPush(SessionPushKind.ActiveChanged, "live-other"));
        time.Advance(TimeSpan.FromSeconds(29));
        await Task.Delay(100);
        await Assert.That(listing.Calls.Count).IsEqualTo(calls);

        listing.Answer("ended", "me", 0, 212, Rows("s", 1, 20));
        time.Advance(TimeSpan.FromSeconds(2));
        await Eventually(() => service.Ended.Count == 20, "the held reconcile");
        await Task.Delay(100);
        await Assert.That(listing.Count("active", "me", 0)).IsEqualTo(2);
    }

    /// A refused reconcile goes again after its hold, so a zero Retry-After still waits a second
    /// rather than re-requesting at once.
    [Test]
    public async Task A_zero_retry_time_still_holds_for_a_second() {
        var (lane, listing, time, service) = Build();
        using var _ = service;
        ScriptDefault(listing);
        // Yields, so a read that re-requests without holding spins off the test's thread.
        listing.Gate = async _ => await Task.Yield();
        listing.Refuse("ended", "me", 0, TimeSpan.Zero);
        Connect(lane);
        await Eventually(() => listing.Count("ended", "me", 0) == 1, "refused page");
        await Task.Delay(100);
        await Assert.That(listing.Count("ended", "me", 0)).IsEqualTo(1);

        time.Advance(TimeSpan.FromSeconds(1));
        await Eventually(() => listing.Count("ended", "me", 0) == 2, "the held reconcile");
    }

    /// The hold is the refused account's budget: signing in as another account reads at once.
    [Test]
    public async Task Another_account_does_not_wait_on_the_previous_accounts_hold() {
        var (lane, listing, _, service) = Build();
        using var _ = service;
        ScriptDefault(listing);
        listing.Gate = async _ => await Task.Yield();
        listing.Refuse("ended", "me", 0, TimeSpan.FromMinutes(10));
        Connect(lane, "u1");
        await Eventually(() => listing.Count("ended", "me", 0) == 1, "refused page");
        await Eventually(() => service.Live.Count == 2, "the refused read published, its hold set");

        listing.Answer("ended", "me", 0, 212, Rows("s", 1, 20));
        Connect(lane, "u2", epoch: 2);

        await Eventually(() => service.Ended.Count == 20, "the new account's read");
    }

    /// A refusal that answers the previous account's read, landing after the switch, holds nothing.
    [Test]
    public async Task A_refusal_for_the_previous_account_sets_no_hold() {
        var (lane, listing, _, service) = Build();
        using var _ = service;
        ScriptDefault(listing);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var endedReads = 0;
        // The first account's read is held open and refused; the new account's is answered.
        listing.Gate = async query => {
            if (query.State == "ended" && Interlocked.Increment(ref endedReads) == 1) await release.Task;
            else if (query.State == "ended") listing.Answer("ended", "me", 0, 212, Rows("s", 1, 20));
            await Task.Yield();
        };
        listing.Refuse("ended", "me", 0, TimeSpan.FromMinutes(10));
        Connect(lane, "u1");
        await Eventually(() => listing.Count("ended", "me", 0) == 1, "the first account's read in flight");

        Connect(lane, "u2", epoch: 2);
        release.SetResult();

        await Eventually(() => service.Ended.Count == 20, "the new account's read");
    }

    [Test]
    public async Task At_cap_show_more_is_refused() {
        var (lane, listing, _, service) = Build();
        using var _ = service;
        ScriptDefault(listing, endedTotal: 1000, pagesScripted: 25);
        ServerSessionsPaging? paging = null;
        using var sub = service.Paging.Subscribe(p => paging = p);
        Connect(lane);
        await Eventually(() => service.Ended.Count == 20, "first page");

        for (var i = 1; i < 25; i++) await service.ShowMoreAsync();
        await Eventually(() => service.Ended.Count == 500, "twenty-five pages");
        await Eventually(() => paging is { Pages: 25 }, "paging at cap");
        await Assert.That(paging!.AtCap).IsTrue();
        await Assert.That(paging.CanShowMore).IsFalse();

        var calls = listing.Calls.Count;
        await service.ShowMoreAsync();
        await Assert.That(listing.Calls.Count).IsEqualTo(calls);
    }

    /// The rail can change scope while the app tears the service down.
    [Test]
    public async Task Scope_changes_after_dispose_are_ignored() {
        var (_, listing, _, service) = Build();
        service.Dispose();

        service.Scope = RailScope.Team;

        await Assert.That(listing.Calls).IsEmpty();
    }

    [Test]
    public async Task Team_scope_reads_one_live_listing_and_unfiltered_ended_pages() {
        var (lane, listing, _, service) = Build();
        using var _ = service;
        listing.Answer("active", null, 0, 2, Row("live-me", "active"), Row("live-other", "active", "other"));
        listing.Answer("ended", null, 0, 2, Row("e1"), Row("e2", owner: "other"));
        service.Scope = RailScope.Team;

        Connect(lane);

        await Eventually(() => service.Ended.Count == 2, "team ended page");
        await Assert.That(listing.Count("active", "me", 0)).IsEqualTo(0);
        await Assert.That(listing.Count("ended", null, 0)).IsEqualTo(1);
        await Assert.That(service.MineLive.Count).IsEqualTo(0);
        await Assert.That(service.Live.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Live_beyond_the_cap_reports_the_remainder() {
        var (lane, listing, _, service) = Build();
        using var _ = service;
        service.Scope = RailScope.Team;
        for (var offset = 0; offset <= 500; offset += 100)
            listing.Answer("active", null, offset, 700, Rows("a", offset + 1, 100, "active"));
        listing.Answer("ended", null, 0, 0);
        var notShown = -1;
        using var sub = service.LiveNotShown.Subscribe(n => notShown = n);

        Connect(lane);

        await Eventually(() => service.Live.Count == 600, "six hundred live rows");
        await Eventually(() => notShown == 100, "remainder reported");
        await Assert.That(listing.Calls.Count(q => q.State == "active")).IsEqualTo(6);
    }

    [Test]
    public async Task A_subject_change_clears_every_cache_before_the_new_read() {
        var (lane, listing, _, service) = Build(ct => Task.FromResult<SessionOwnerOptionDto[]?>(Owners("me")));
        using var _ = service;
        ScriptDefault(listing);
        Connect(lane, "u1", 1);
        await Eventually(() => service.Ended.Count == 20 && service.Live.Count == 2, "u1 rows");
        var idCounts = new List<int>();
        using var idsSub = service.ViewerIds.Subscribe(v => { lock (idCounts) idCounts.Add(v.Count); });
        await Eventually(() => { lock (idCounts) return idCounts.Count > 0 && idCounts[^1] == 1; }, "u1 ids");

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        listing.Gate = _ => gate.Task;
        Connect(lane, "u2", 2);

        await Eventually(() => service.Ended.Count == 0 && service.Live.Count == 0 && service.MineLive.Count == 0, "caches cleared");
        // The ids are emptied at the switch and only then re-read for the new account.
        await Eventually(() => { lock (idCounts) return idCounts.Contains(0); }, "ids cleared at the switch");
        int[] counts;
        lock (idCounts) counts = [.. idCounts];
        var cleared = Array.IndexOf(counts, 0);
        await Assert.That(counts.Take(cleared).All(c => c == 1)).IsTrue();
        await Assert.That(counts.Skip(cleared + 1).All(c => c == 1)).IsTrue();

        listing.Gate = null;
        listing.Answer("ended", "me", 0, 1, Row("u2-only"));
        gate.SetResult();
        await Eventually(() => service.Ended.Count == 1, "u2 rows");
        await Assert.That(service.Ended.Lookup("u2-only").HasValue).IsTrue();
    }

    [Test]
    public async Task A_completion_from_an_older_connection_publishes_nothing() {
        var (lane, listing, _, service) = Build();
        using var _ = service;
        ScriptDefault(listing);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The live reads pass and return u1's rows; the ended read is held open across the reconnect.
        listing.Gate = q => q is { State: "ended", Offset: 0 } ? gate.Task : Task.CompletedTask;
        Connect(lane, "u1", 1);
        await Eventually(() => listing.Count("ended", "me", 0) == 1, "u1 ended read in flight");

        var afterSwitch = false;
        var staleLivePublished = false;
        using var watch = service.Live.Connect().Subscribe(changes => {
            if (afterSwitch && changes.Any(c => c.Key == "live-me" && c.Reason == ChangeReason.Add)) staleLivePublished = true;
        });
        listing.Gate = null;
        listing.Answer("active", "me", 0, 1, Row("u2-live", "active"));
        listing.Answer("active", null, 0, 1, Row("u2-live", "active"));
        listing.Answer("ended", "me", 0, 1, Row("u2-only"));
        afterSwitch = true;
        Connect(lane, "u2", 2);
        gate.SetResult();

        await Eventually(() => service.Ended.Lookup("u2-only").HasValue && service.Live.Lookup("u2-live").HasValue, "u2 rows");
        await Assert.That(service.Live.Count).IsEqualTo(1);
        await Assert.That(service.Ended.Count).IsEqualTo(1);
        await Assert.That(staleLivePublished).IsFalse();
    }

    [Test]
    public async Task Current_follows_reads_and_the_lane() {
        var (lane, listing, _, service) = Build();
        using var _ = service;
        ScriptDefault(listing);
        var seen = new List<bool>();
        using var sub = service.Current.Subscribe(seen.Add);

        Connect(lane);
        await Eventually(() => seen.Contains(true), "current after connect");
        lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying));
        await Assert.That(seen[^1]).IsFalse();

        listing.Fail("ended", "me", 0);
        Connect(lane, "u1", 2);
        await Eventually(() => listing.Count("ended", "me", 0) == 2, "second read");
        await Eventually(() => seen[^1] == false, "stale after a failed read");
    }

    [Test]
    public async Task Owners_resolve_the_viewer_ids() {
        var (lane, listing, _, service) = Build(_ => Task.FromResult<SessionOwnerOptionDto[]?>(Owners("a", "b")));
        using var _ = service;
        ScriptDefault(listing);
        IReadOnlySet<string>? ids = null;
        string? user = null;
        using var idsSub = service.ViewerIds.Subscribe(v => ids = v);
        using var userSub = service.ViewerUserId.Subscribe(u => user = u);

        Connect(lane);

        await Eventually(() => ids is { Count: 2 }, "ids");
        await Assert.That(ids!.Contains("a") && ids.Contains("b")).IsTrue();
        await Assert.That(user).IsEqualTo("a");
    }

    [Test]
    public async Task Show_more_during_a_reconcile_waits_and_runs_once_after() {
        var (lane, listing, _, service) = Build();
        using var _ = service;
        ScriptDefault(listing);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        listing.Gate = q => q is { State: "ended", Offset: 0 } ? gate.Task : Task.CompletedTask;
        Connect(lane);
        await Eventually(() => listing.Count("ended", "me", 0) == 1, "first read in flight");

        var showMore = service.ShowMoreAsync();
        await Task.Delay(50);
        await Assert.That(listing.Count("ended", "me", 20)).IsEqualTo(0);

        listing.Gate = null;
        gate.SetResult();
        await showMore;
        await Eventually(() => service.Ended.Count == 40, "two pages after the gate");
        await Assert.That(listing.Count("ended", "me", 20)).IsEqualTo(1);
        await Assert.That(service.Ended.Keys.Distinct(StringComparer.Ordinal).Count()).IsEqualTo(40);
    }

    /// A live session's summary changes often; such a push re-reads the live listing alone, never the prefix.
    [Test]
    public async Task An_active_session_change_rereads_only_the_live_listing() {
        var (lane, listing, _, service) = Build();
        using var _ = service;
        ScriptDefault(listing);
        Connect(lane);
        await Eventually(() => service.Ended.Count == 20, "first page");

        lane.SessionPushesSubject.OnNext(new SessionPush(SessionPushKind.ActiveChanged, "live-me"));

        await Eventually(() => listing.Count("active", null, 0) == 2, "live re-read");
        await Task.Delay(100);
        await Assert.That(listing.Count("ended", "me", 0)).IsEqualTo(1);
        await Assert.That(service.Ended.Count).IsEqualTo(20);
    }

    /// The server announces an end only after its removal delay, while a live re-read already stops
    /// listing the session: that read goes on to the ended prefix, so the session is never in
    /// neither set.
    [Test]
    public async Task A_live_read_that_drops_an_unlisted_session_reads_the_ended_prefix_before_publishing() {
        var (lane, listing, _, service) = Build();
        using var _ = service;
        ScriptDefault(listing);
        var seen = new List<ServerSessionsSnapshot>();
        Connect(lane);
        await Eventually(() => service.Ended.Count == 20, "first page");
        using var sub = service.Snapshots.Subscribe(s => { lock (seen) seen.Add(s); });

        listing.Answer("active", "me", 0, 0);
        listing.Answer("active", null, 0, 1, Row("live-other", "active", "other"));
        listing.Answer("ended", "me", 0, 213, [Row("live-me"), .. Rows("s", 1, 19)]);
        lane.SessionPushesSubject.OnNext(new SessionPush(SessionPushKind.ActiveChanged, "live-me"));

        await Eventually(() => service.Ended.Lookup("live-me").HasValue, "the ended row");
        await Assert.That(listing.Count("ended", "me", 0)).IsEqualTo(2);
        List<ServerSessionsSnapshot> published;
        lock (seen) published = [.. seen];
        await Assert.That(published.All(s => s.Live.Concat(s.MineLive).Concat(s.Ended).Any(r => r.SessionId == "live-me"))).IsTrue();
    }

    /// The directory recomputes on every publication, so one read is one publication of all three sets.
    [Test]
    public async Task A_read_publishes_live_mine_and_ended_in_one_snapshot() {
        var (lane, listing, _, service) = Build();
        using var _ = service;
        ScriptDefault(listing);
        var seen = new List<ServerSessionsSnapshot>();
        using var sub = service.Snapshots.Subscribe(seen.Add);

        Connect(lane);

        await Eventually(() => seen.Any(s => s.Ended.Count == 20), "the read's snapshot");
        var published = seen.Where(s => s.Live.Count > 0 || s.Ended.Count > 0).ToList();
        await Assert.That(published).Count().IsEqualTo(1);
        await Assert.That(published[0].Live.Count).IsEqualTo(2);
        await Assert.That(published[0].MineLive.Count).IsEqualTo(1);
    }

    /// A scope switched while a read is in flight makes that read's rows another scope's.
    [Test]
    public async Task A_read_taken_under_the_previous_scope_publishes_nothing() {
        var (lane, listing, _, service) = Build();
        using var _ = service;
        ScriptDefault(listing);
        listing.Answer("ended", null, 0, 2, Row("t1", owner: "other"), Row("t2", owner: "other"));
        Connect(lane);
        await Eventually(() => service.Ended.Count == 20, "mine prefix");
        var seen = new List<ServerSessionsSnapshot>();
        using var sub = service.Snapshots.Subscribe(seen.Add);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        listing.Gate = q => q is { State: "ended", Owner: null, Offset: 0 } ? gate.Task : Task.CompletedTask;

        service.Scope = RailScope.Team;
        await Eventually(() => listing.Count("ended", null, 0) == 1, "team read in flight");
        service.Scope = RailScope.Mine;
        listing.Gate = null;
        gate.SetResult();

        await Eventually(() => listing.Count("ended", "me", 0) >= 2 && service.Ended.Count == 20, "mine re-read");
        await Assert.That(seen.Any(s => s.Ended.Any(r => r.SessionId == "t1"))).IsFalse();
    }

    /// The rail's open row is exempt from the page cut while it stays selected.
    [Test]
    public async Task The_kept_session_survives_a_read_that_no_longer_lists_it() {
        var (lane, listing, _, service) = Build();
        using var _ = service;
        ScriptDefault(listing, endedTotal: 20, pagesScripted: 1);
        Connect(lane);
        await Eventually(() => service.Ended.Count == 20, "first page");
        ServerSessionsSnapshot? latest = null;
        using var sub = service.Snapshots.Subscribe(s => latest = s);

        service.Keep("s1");
        listing.Answer("ended", "me", 0, 20, Rows("s", 2, 20));
        lane.SessionPushesSubject.OnNext(new SessionPush(SessionPushKind.ActiveRemoved, "live-me"));
        await Eventually(() => latest is { } s && s.Ended.Any(r => r.SessionId == "s21"), "re-read");
        await Assert.That(latest!.Ended.Any(r => r.SessionId == "s1")).IsTrue();

        service.Keep(null);
        await Eventually(() => latest is { } s && !s.Ended.Any(r => r.SessionId == "s1"), "dropped on deselection");
    }

    /// A read admitted before the deletion still lists the session; its publication must not bring it back.
    [Test]
    public async Task A_deleted_session_does_not_return_from_a_read_already_in_flight() {
        var (lane, listing, _, service) = Build();
        using var _ = service;
        ScriptDefault(listing);
        Connect(lane);
        await Eventually(() => service.Ended.Count == 20, "first page");
        var seen = new List<ServerSessionsSnapshot>();
        using var sub = service.Snapshots.Subscribe(seen.Add);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        listing.Gate = q => q is { State: "ended", Offset: 0 } ? gate.Task : Task.CompletedTask;
        service.Keep("s3");
        lane.SessionPushesSubject.OnNext(new SessionPush(SessionPushKind.ActiveRemoved, "live-me"));
        await Eventually(() => listing.Count("ended", "me", 0) == 2, "re-read in flight");

        lane.SessionPushesSubject.OnNext(new SessionPush(SessionPushKind.Deleted, "s3"));
        await Eventually(() => !service.Ended.Lookup("s3").HasValue, "dropped at once");
        var before = seen.Count;
        listing.Gate = null;
        gate.SetResult();

        // The deletion also asks for a fresh read, whose scripted answer still lists s3; only the
        // read admitted before the deletion is under test here.
        await Eventually(() => seen.Count > before, "the in-flight read published");
        await Assert.That(seen[before].Ended.Any(r => r.SessionId == "s3")).IsFalse();
    }

    /// A read that outlives its connection reports on a lane that is no longer there.
    [Test]
    public async Task A_read_finishing_after_the_lane_drops_does_not_report_current() {
        var (lane, listing, _, service) = Build();
        using var _ = service;
        ScriptDefault(listing);
        var seen = new List<bool>();
        using var sub = service.Current.Subscribe(seen.Add);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        listing.Gate = q => q is { State: "ended", Offset: 0 } ? gate.Task : Task.CompletedTask;

        Connect(lane);
        await Eventually(() => listing.Count("ended", "me", 0) == 1, "read in flight");
        lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying));
        listing.Gate = null;
        gate.SetResult();

        await Task.Delay(100);
        await Assert.That(seen).DoesNotContain(true);
    }

    /// The drop is announced before the snapshot without the row: a consumer acting on the removal
    /// alone would otherwise treat a daemon row the removal exposes as a takeover.
    [Test]
    public async Task A_deletion_is_announced_before_the_snapshot_that_lacks_it() {
        var (lane, listing, _, service) = Build();
        using var _ = service;
        ScriptDefault(listing);
        Connect(lane);
        await Eventually(() => service.Ended.Count == 20, "first page");
        var order = new List<string>();
        using var dropped = service.Dropped.Subscribe(id => order.Add($"dropped {id}"));
        using var snapshots = service.Snapshots.Where(s => s.Ended.All(r => r.SessionId != "s3")).Take(1).Subscribe(_ => order.Add("snapshot"));

        lane.SessionPushesSubject.OnNext(new SessionPush(SessionPushKind.Deleted, "s3"));

        await Eventually(() => order.Count >= 2, "both announcements");
        await Assert.That(order[0]).IsEqualTo("dropped s3");
        await Assert.That(order[1]).IsEqualTo("snapshot");
    }

    /// A paging request is answered by the read admitted after it, whatever refreshes queue behind.
    [Test]
    public async Task A_paging_request_completes_while_pushes_keep_arriving() {
        var (lane, listing, _, service) = Build();
        using var _ = service;
        ScriptDefault(listing);
        Connect(lane);
        await Eventually(() => service.Ended.Count == 20, "first page");
        var liveReads = listing.Count("active", "me", 0);
        var pageGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rerunGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        listing.Gate = q => q switch {
            { State: "ended", Offset: 20 } => pageGate.Task,
            { State: "active", Owner: "me" } when listing.Count("active", "me", 0) > liveReads + 1 => rerunGate.Task,
            _ => Task.CompletedTask,
        };

        var more = service.ShowMoreAsync();
        await Eventually(() => listing.Count("ended", "me", 20) == 1, "second page in flight");
        lane.SessionPushesSubject.OnNext(new SessionPush(SessionPushKind.ActiveRemoved, "live-me"));
        await Task.Delay(50);
        pageGate.SetResult();

        await more.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.That(service.Ended.Count).IsEqualTo(40);
        rerunGate.SetResult();
    }

    [Test]
    public async Task Deleted_push_removes_the_row_at_once() {
        var (lane, listing, _, service) = Build();
        using var _ = service;
        ScriptDefault(listing);
        Connect(lane);
        await Eventually(() => service.Ended.Count == 20, "first page");

        // The server no longer lists it either, so the reconcile the push also triggers agrees.
        listing.Answer("ended", "me", 0, 211, [.. Rows("s", 1, 21).Where(r => r.SessionId != "s3")]);
        lane.SessionPushesSubject.OnNext(new SessionPush(SessionPushKind.Deleted, "s3"));

        await Eventually(() => !service.Ended.Lookup("s3").HasValue, "deleted row gone");
        await Eventually(() => listing.Count("ended", "me", 0) == 2, "prefix backfilled");
        await Eventually(() => service.Ended.Count == 20, "next-oldest row filled the page");
    }

    /// Retention exempts the open row from the page cut, not from whose sessions the scope shows.
    [Test]
    public async Task The_kept_row_does_not_cross_a_scope_change() {
        var (lane, listing, _, service) = Build();
        using var _ = service;
        ScriptDefault(listing);
        listing.Answer("ended", null, 0, 2, Row("t1", owner: "other"), Row("t2", owner: "other"));
        Connect(lane);
        await Eventually(() => service.Ended.Count == 20, "mine prefix");
        service.Scope = RailScope.Team;
        await Eventually(() => service.Ended.Lookup("t1").HasValue, "team prefix");
        service.Keep("t1");
        ServerSessionsSnapshot? latest = null;
        using var sub = service.Snapshots.Subscribe(s => latest = s);

        service.Scope = RailScope.Mine;

        await Assert.That(latest!.Ended.Any(r => r.SessionId == "t1")).IsFalse();
        await Eventually(() => service.Ended.Count == 20, "mine re-read");
        await Assert.That(latest!.Ended.Any(r => r.SessionId == "t1")).IsFalse();
    }

    /// A debounce waits for a quiet an active org never gives; the live re-read runs once per window instead.
    [Test]
    public async Task A_steady_stream_of_changes_still_rereads_the_live_listing_each_window() {
        var scheduler = new HistoricalScheduler();
        var (lane, listing, _, service) = Build(pushDebounce: TimeSpan.FromMilliseconds(250), pushScheduler: scheduler);
        using var _ = service;
        ScriptDefault(listing);
        Connect(lane);
        await Eventually(() => service.Ended.Count == 20, "first read");

        for (var i = 0; i < 10; i++) {
            lane.SessionPushesSubject.OnNext(new SessionPush(SessionPushKind.ActiveChanged, "live-me"));
            scheduler.AdvanceBy(TimeSpan.FromMilliseconds(100));
        }

        await Eventually(() => listing.Count("active", null, 0) >= 4, "live re-reads during the stream");
    }

    /// The server pushes every live session of the tenant to every client, and each re-read is
    /// several listing pages, so the default live window is seconds; the ended lane's short
    /// throttle stays, since an ending or a rename is rare and moves the prefix.
    [Test]
    public async Task The_default_live_window_rereads_at_most_once_in_five_seconds() {
        var scheduler = new HistoricalScheduler();
        var lane = new FakeServerLane();
        var listing = new ScriptedListing();
        using var service = new ServerSessionsService(
            lane, listing.Fetch, _ => Task.FromResult<SessionOwnerOptionDto[]?>(Owners("me")), new FakeTimeProvider(), pushScheduler: scheduler);
        ScriptDefault(listing);
        Connect(lane);
        await Eventually(() => service.Ended.Count == 20, "first read");
        var reads = listing.Count("active", null, 0);

        for (var i = 0; i < 20; i++) {
            lane.SessionPushesSubject.OnNext(new SessionPush(SessionPushKind.ActiveChanged, "live-me"));
            scheduler.AdvanceBy(TimeSpan.FromMilliseconds(200));
        }
        await Task.Delay(100);
        await Assert.That(listing.Count("active", null, 0)).IsEqualTo(reads);

        scheduler.AdvanceBy(TimeSpan.FromSeconds(1));
        await Eventually(() => listing.Count("active", null, 0) == reads + 1, "one re-read per window");
        lane.SessionPushesSubject.OnNext(new SessionPush(SessionPushKind.ActiveRemoved, "live-me"));
        scheduler.AdvanceBy(TimeSpan.FromMilliseconds(250));
        await Eventually(() => listing.Count("ended", "me", 0) == 2, "an ending re-reads the prefix within its own short window");
    }

    /// A read admitted after the lane dropped is as stale as one that outlived it.
    [Test]
    public async Task A_read_admitted_while_disconnected_publishes_nothing() {
        var (lane, listing, _, service) = Build();
        using var _ = service;
        ScriptDefault(listing);
        var seen = new List<bool>();
        using var sub = service.Current.Subscribe(seen.Add);
        Connect(lane);
        await Eventually(() => service.Ended.Count == 20, "first read");
        lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying));

        await service.ShowMoreAsync();

        await Assert.That(service.Ended.Count).IsEqualTo(20);
        await Assert.That(seen[^1]).IsFalse();
    }

    /// One owners read at a time: a slow one is not joined by the cadence's retries.
    [Test]
    public async Task The_owners_retry_waits_for_the_read_in_flight() {
        var attempts = 0;
        var gate = new TaskCompletionSource<SessionOwnerOptionDto[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (lane, listing, time, service) = Build(owners: _ => { attempts++; return gate.Task; }, reconcileEvery: TimeSpan.FromSeconds(60));
        using var _ = service;
        ScriptDefault(listing);
        IReadOnlySet<string>? ids = null;
        using var sub = service.ViewerIds.Subscribe(v => ids = v);
        Connect(lane);
        await Eventually(() => service.Ended.Count == 20, "first read");

        time.Advance(TimeSpan.FromSeconds(60));
        time.Advance(TimeSpan.FromSeconds(60));
        await Task.Delay(50);
        await Assert.That(attempts).IsEqualTo(1);

        gate.SetResult(Owners("me"));
        await Eventually(() => ids!.Count > 0, "owners resolved");
    }

    /// A reconnect while an owners read is pending is not left to the cadence: the stale read's end
    /// starts the new connection's read at once.
    [Test]
    public async Task A_stale_owners_read_hands_over_to_the_new_connections_read_at_once() {
        var attempts = 0;
        var gates = new List<TaskCompletionSource<SessionOwnerOptionDto[]?>>();
        var (lane, listing, _, service) = Build(owners: _ => {
            attempts++;
            var gate = new TaskCompletionSource<SessionOwnerOptionDto[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gates) gates.Add(gate);
            return gate.Task;
        });
        using var _ = service;
        ScriptDefault(listing);
        IReadOnlySet<string>? ids = null;
        using var sub = service.ViewerIds.Subscribe(v => ids = v);
        Connect(lane, epoch: 1);
        await Eventually(() => attempts == 1, "first owners read");
        Connect(lane, epoch: 2);
        await Eventually(() => listing.Count("ended", "me", 0) == 2, "the second connection's listing read");

        gates[0].SetResult(Owners("me"));

        await Eventually(() => attempts == 2, "the new connection's owners read");
        await Assert.That(ids!.Count).IsEqualTo(0);
        gates[1].SetResult(Owners("me"));
        await Eventually(() => ids!.Count > 0, "resolved for the new connection");
    }

    /// Under Mine the unfiltered live set is ownership evidence; the previous scope's copy would
    /// hide the viewer's own registry rows as "listed but not mine" until the owner-filtered read lands.
    [Test]
    public async Task Switching_to_Mine_clears_the_live_set_until_its_own_read_lands() {
        var (lane, listing, _, service) = Build();
        using var _ = service;
        ScriptDefault(listing);
        listing.Answer("ended", null, 0, 0);
        Connect(lane);
        await Eventually(() => service.Ended.Count == 20, "mine prefix");
        service.Scope = RailScope.Team;
        await Eventually(() => service.Live.Count == 2, "team live set");
        var seen = new List<ServerSessionsSnapshot>();
        using var sub = service.Snapshots.Subscribe(seen.Add);
        var before = seen.Count;

        service.Scope = RailScope.Mine;

        await Assert.That(seen.Skip(before).Any(s => s.Live.Count == 0 && s.MineLive.Count == 0)).IsTrue();
        await Eventually(() => service.MineLive.Count == 1 && service.Live.Count == 2, "mine read lands");
    }

    /// The viewer stays unresolved only as long as the owners read keeps failing.
    [Test]
    public async Task A_failed_owners_read_is_retried_on_the_cadence() {
        var attempts = 0;
        var (lane, listing, time, service) = Build(
            owners: _ => Task.FromResult<SessionOwnerOptionDto[]?>(++attempts == 1 ? null : Owners("me")),
            reconcileEvery: TimeSpan.FromSeconds(60));
        using var _ = service;
        ScriptDefault(listing);
        IReadOnlySet<string>? ids = null;
        using var sub = service.ViewerIds.Subscribe(v => ids = v);
        Connect(lane);
        await Eventually(() => service.Ended.Count == 20, "first read");
        await Assert.That(ids!.Count).IsEqualTo(0);

        time.Advance(TimeSpan.FromSeconds(60));

        await Eventually(() => ids!.Count > 0, "owners retried");
    }


    [Test]
    public async Task The_cadence_reconciles_while_connected_only() {
        var (lane, listing, time, service) = Build(reconcileEvery: TimeSpan.FromSeconds(60));
        using var _ = service;
        ScriptDefault(listing);
        Connect(lane);
        await Eventually(() => service.Ended.Count == 20, "first page");
        var reads = listing.Count("ended", "me", 0);

        time.Advance(TimeSpan.FromSeconds(61));
        await Eventually(() => listing.Count("ended", "me", 0) == reads + 1, "cadence read");

        lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying));
        time.Advance(TimeSpan.FromSeconds(61));
        await Task.Delay(50);
        await Assert.That(listing.Count("ended", "me", 0)).IsEqualTo(reads + 1);
    }

    static string? TitleIn(IObservableCache<RepoSessionDto, string> cache, string id) => cache.Lookup(id).Value.Title;

    [Test]
    public async Task An_applied_rename_shows_in_every_set_at_once_and_holds_over_a_lagging_read() {
        var (lane, listing, _, service) = Build();
        using var _ = service;
        ScriptDefault(listing);
        Connect(lane);
        await Eventually(() => service.Ended.Count == 20 && service.MineLive.Count == 1, "first read");

        service.ApplyRename("live-me", "Renamed");
        service.ApplyRename("s1", "First");
        service.ApplyRename("s1", "Second");

        await Assert.That(TitleIn(service.Live, "live-me")).IsEqualTo("Renamed");
        await Assert.That(TitleIn(service.MineLive, "live-me")).IsEqualTo("Renamed");
        await Assert.That(TitleIn(service.Ended, "s1")).IsEqualTo("Second");

        listing.Answer("ended", "me", 0, 212, [Row("s1") with { Title = "First" }, .. Rows("s", 2, 19)]);
        await service.ReconcileAsync();
        await Assert.That(TitleIn(service.Live, "live-me")).IsEqualTo("Renamed");
        await Assert.That(TitleIn(service.Ended, "s1")).IsEqualTo("Second");

        listing.Answer("active", "me", 0, 1, Row("live-me", "active") with { Title = "Elsewhere" });
        listing.Answer("active", null, 0, 2, Row("live-me", "active") with { Title = "Elsewhere" }, Row("live-other", "active", "other"));
        await service.ReconcileAsync();
        await Assert.That(TitleIn(service.Live, "live-me")).IsEqualTo("Elsewhere");
        await Assert.That(TitleIn(service.MineLive, "live-me")).IsEqualTo("Elsewhere");
    }

    [Test]
    public async Task An_account_change_drops_the_renames() {
        var (lane, listing, _, service) = Build();
        using var _ = service;
        ScriptDefault(listing);
        Connect(lane, "u1", 1);
        await Eventually(() => service.Ended.Count == 20, "first read");
        service.ApplyRename("s1", "Renamed");

        Connect(lane, "u2", 2);
        await service.ReconcileAsync();
        await Eventually(() => service.Ended.Count == 20, "second account's read");

        await Assert.That(TitleIn(service.Ended, "s1")).IsEqualTo("s1");
    }

    /// One read lists a session in more than one set; a copy still carrying the replaced title is lag,
    /// whichever set it is in, so the rename holds in every set and survives into the next read.
    [Test]
    [Arguments("mine lags")]
    [Arguments("unfiltered lags")]
    public async Task A_rename_holds_while_any_copy_in_the_read_still_lags(string lagging) {
        var (lane, listing, _, service) = Build();
        using var _ = service;
        ScriptDefault(listing);
        Connect(lane);
        await Eventually(() => service.Ended.Count == 20 && service.MineLive.Count == 1, "first read");
        service.ApplyRename("live-me", "Renamed");

        var old = Row("live-me", "active");
        var renamed = old with { Title = "Renamed" };
        listing.Answer("active", "me", 0, 1, lagging == "mine lags" ? old : renamed);
        listing.Answer("active", null, 0, 2, lagging == "mine lags" ? renamed : old, Row("live-other", "active", "other"));
        await service.ReconcileAsync();
        await Assert.That(TitleIn(service.Live, "live-me")).IsEqualTo("Renamed");
        await Assert.That(TitleIn(service.MineLive, "live-me")).IsEqualTo("Renamed");

        listing.Answer("active", "me", 0, 1, old);
        listing.Answer("active", null, 0, 2, old, Row("live-other", "active", "other"));
        await service.ReconcileAsync();
        await Assert.That(TitleIn(service.Live, "live-me")).IsEqualTo("Renamed");
        await Assert.That(TitleIn(service.MineLive, "live-me")).IsEqualTo("Renamed");
    }

    /// A copy with no title has not shown the rename either: the rename holds on every copy.
    [Test]
    [Arguments("mine untitled")]
    [Arguments("unfiltered untitled")]
    public async Task A_rename_holds_while_any_copy_in_the_read_has_no_title(string untitled) {
        var (lane, listing, _, service) = Build();
        using var _ = service;
        ScriptDefault(listing);
        Connect(lane);
        await Eventually(() => service.Ended.Count == 20 && service.MineLive.Count == 1, "first read");
        service.ApplyRename("live-me", "Renamed");

        var blank = Row("live-me", "active") with { Title = null };
        var renamed = blank with { Title = "Renamed" };
        listing.Answer("active", "me", 0, 1, untitled == "mine untitled" ? blank : renamed);
        listing.Answer("active", null, 0, 2, untitled == "mine untitled" ? renamed : blank, Row("live-other", "active", "other"));
        await service.ReconcileAsync();
        await Assert.That(TitleIn(service.Live, "live-me")).IsEqualTo("Renamed");
        await Assert.That(TitleIn(service.MineLive, "live-me")).IsEqualTo("Renamed");

        listing.Answer("active", "me", 0, 1, blank);
        listing.Answer("active", null, 0, 2, blank, Row("live-other", "active", "other"));
        await service.ReconcileAsync();
        await Assert.That(TitleIn(service.Live, "live-me")).IsEqualTo("Renamed");
        await Assert.That(TitleIn(service.MineLive, "live-me")).IsEqualTo("Renamed");
    }
}
