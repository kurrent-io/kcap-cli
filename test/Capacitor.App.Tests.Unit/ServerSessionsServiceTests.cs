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
            Func<CancellationToken, Task<SessionOwnerOptionDto[]?>>? owners = null, TimeSpan? reconcileEvery = null) {
        var lane = new FakeServerLane();
        var listing = new ScriptedListing();
        var time = new FakeTimeProvider();
        var service = new ServerSessionsService(
            lane, listing.Fetch, owners ?? (_ => Task.FromResult<SessionOwnerOptionDto[]?>(Owners("me"))), time,
            pushDebounce: TimeSpan.Zero, reconcileEvery: reconcileEvery);
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
}
