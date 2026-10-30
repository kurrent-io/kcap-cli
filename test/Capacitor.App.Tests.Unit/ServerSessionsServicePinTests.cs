using System.Collections.Immutable;
using Capacitor.App.Services;
using Capacitor.Remote.Models;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

public class ServerSessionsServicePinTests {
    /// A listing whose pages a test rewrites between reads; an unscripted page is an empty success.
    sealed class Listing {
        readonly Lock _lock = new();
        readonly Dictionary<(string State, string? Owner), RepoSessionDto[]> _rows = new();

        public void Set(string state, string? owner, params RepoSessionDto[] rows) {
            lock (_lock) _rows[(state, owner)] = rows;
        }

        public Task<ServerSessionsFetch> Fetch(ServerSessionsQuery query, CancellationToken ct) {
            lock (_lock) {
                var rows = query.Offset == 0 && _rows.TryGetValue((query.State, query.Owner), out var r) ? r : [];
                return Task.FromResult(new ServerSessionsFetch(rows, rows.Length));
            }
        }
    }

    /// Every summary read is held until the test answers it by its call index.
    sealed class Summaries {
        ImmutableList<(string Id, TaskCompletionSource<SummaryFetch> Answer)> _calls = [];

        public ImmutableList<(string Id, TaskCompletionSource<SummaryFetch> Answer)> Calls => Volatile.Read(ref _calls);

        public Task<SummaryFetch> Fetch(string id, CancellationToken ct) {
            var answer = new TaskCompletionSource<SummaryFetch>();
            ImmutableInterlocked.Update(ref _calls, l => l.Add((id, answer)));
            return answer.Task;
        }

        public void Answer(int call, ActivitySessionDto summary) => Calls[call].Answer.SetResult(new(ServerReadOutcome.Ok, summary));
        public void Fail(int call, ServerReadOutcome outcome) => Calls[call].Answer.SetResult(new(outcome, null));
    }

    sealed class Rig : IDisposable {
        public readonly FakeServerLane Lane = new();
        public readonly Listing Listing = new();
        public readonly Summaries Summaries = new();
        public readonly FakeTimeProvider Time = new();
        public readonly ServerSessionsService Service;
        ImmutableList<ServerSessionsSnapshot> _snapshots = [];
        readonly IDisposable _subscription;

        public Rig() {
            Service = new ServerSessionsService(
                Lane, Listing.Fetch, _ => Task.FromResult<SessionOwnerOptionDto[]?>([new() { UserId = "me", IsMe = true, Ids = ["me"] }]),
                Time, pushDebounce: TimeSpan.Zero, fetchSummary: Summaries.Fetch);
            _subscription = Service.Snapshots.Subscribe(s => ImmutableInterlocked.Update(ref _snapshots, l => l.Add(s)));
        }

        public ImmutableList<ServerSessionsSnapshot> Snapshots => Volatile.Read(ref _snapshots);
        public ServerSessionsSnapshot Latest => Snapshots[^1];

        public void Connect(string subject = "u1", int epoch = 1) =>
            Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: subject, Epoch: epoch));

        public void Push(SessionPushKind kind, string id) => Lane.SessionPushesSubject.OnNext(new SessionPush(kind, id));

        /// Pins an unlisted session, answering its first summary read.
        public async Task PinUnlistedAsync(string id, ActivitySessionDto summary) {
            var calls = Summaries.Calls.Count;
            var pin = Service.PinAsync(id, Presentation, CancellationToken.None);
            await Eventually(() => Summaries.Calls.Count == calls + 1, "initial summary read");
            Summaries.Answer(calls, summary);
            await Assert.That(await pin).IsEqualTo(PinOutcome.Pinned);
        }

        public void Dispose() {
            _subscription.Dispose();
            Service.Dispose();
        }
    }

    static readonly PinPresentation Presentation = new("bob", "Bob B", "https://b/avatar");

    static RepoSessionDto Row(string id, string status = "ended", string owner = "me") => new() {
        SessionId = id, Status = status, AccessLevel = "full", Vendor = "claude", Title = id,
        Owner = new() { UserId = owner, DisplayName = owner },
        StartedAt = DateTimeOffset.UnixEpoch, LastActivityAt = DateTimeOffset.UnixEpoch,
        EndedAt = status == "ended" ? DateTimeOffset.UnixEpoch : null,
    };

    static ActivitySessionDto Summary(string id, string status = "active", string? title = null) => new() {
        SessionId = id, Status = status, Title = title ?? id, Vendor = "claude", OwnerUserId = "teammate",
        StartedAt = DateTimeOffset.UnixEpoch, EndedAt = status == "ended" ? DateTimeOffset.UnixEpoch.AddHours(1) : null,
    };

    static async Task Eventually(Func<bool> condition, string what, int ms = 5000) {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for: {what}");
            await Task.Delay(10);
        }
    }

    [Test]
    public async Task Pin_of_a_listed_session_reads_nothing_and_publishes_no_pinned_row() {
        using var rig = new Rig();
        rig.Listing.Set("ended", "me", Row("s1"));
        rig.Connect();
        await Eventually(() => rig.Service.Ended.Count == 1, "listed");

        var outcome = await rig.Service.PinAsync("s1", Presentation, CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(PinOutcome.Pinned);
        await Assert.That(rig.Summaries.Calls).IsEmpty();
        await Assert.That(rig.Service.PinnedSessionId).IsEqualTo("s1");
        await Assert.That(rig.Latest.Pinned).IsNull();
        await Assert.That(rig.Latest.PinnedSessionId).IsEqualTo("s1");
    }

    [Test]
    public async Task Pin_of_an_unlisted_session_publishes_the_summary_row_on_the_snapshot() {
        using var rig = new Rig();
        rig.Connect();

        await rig.PinUnlistedAsync("x", Summary("x", title: "Teammate work"));

        var pinned = rig.Latest.Pinned;
        await Assert.That(pinned).IsNotNull();
        await Assert.That(pinned!.SessionId).IsEqualTo("x");
        await Assert.That(pinned.Title).IsEqualTo("Teammate work");
        await Assert.That(pinned.Owner!.UserId).IsEqualTo("teammate");
        await Assert.That(pinned.Owner.Username).IsEqualTo("bob");
        await Assert.That(rig.Latest.PinnedSessionId).IsEqualTo("x");
        await Assert.That(rig.Summaries.Calls.Single().Id).IsEqualTo("x");
    }

    [Test]
    public async Task Pin_survives_a_scope_change() {
        using var rig = new Rig();
        rig.Connect();
        await rig.PinUnlistedAsync("x", Summary("x"));

        rig.Service.Scope = RailScope.Team;
        await rig.Service.ReconcileAsync();

        await Assert.That(rig.Service.PinnedSessionId).IsEqualTo("x");
        await Assert.That(rig.Latest.Pinned?.SessionId).IsEqualTo("x");
        await Assert.That(rig.Latest.PinnedSessionId).IsEqualTo("x");
    }

    [Test]
    public async Task Account_change_drops_the_pin_and_a_late_summary_publishes_nothing() {
        using var rig = new Rig();
        rig.Connect();
        var pin = rig.Service.PinAsync("x", Presentation, CancellationToken.None);
        await Eventually(() => rig.Summaries.Calls.Count == 1, "summary read");

        rig.Connect("u2", epoch: 2);
        rig.Summaries.Answer(0, Summary("x"));

        await Assert.That(await pin).IsEqualTo(PinOutcome.Superseded);
        await Assert.That(rig.Latest.Pinned).IsNull();
        await Assert.That(rig.Latest.PinnedSessionId).IsNull();
        await Assert.That(rig.Service.PinnedSessionId).IsNull();
    }

    [Test]
    public async Task Deleted_push_during_the_initial_read_returns_unavailable() {
        using var rig = new Rig();
        rig.Connect();
        var pin = rig.Service.PinAsync("x", Presentation, CancellationToken.None);
        await Eventually(() => rig.Summaries.Calls.Count == 1, "summary read");

        rig.Push(SessionPushKind.Deleted, "x");
        rig.Summaries.Answer(0, Summary("x"));

        await Assert.That(await pin).IsEqualTo(PinOutcome.Unavailable);
        await Assert.That(rig.Latest.Pinned).IsNull();
        await Assert.That(rig.Service.PinnedSessionId).IsNull();
    }

    [Test]
    public async Task Access_change_during_the_initial_read_reissues_the_read() {
        using var rig = new Rig();
        rig.Connect();
        var pin = rig.Service.PinAsync("x", Presentation, CancellationToken.None);
        await Eventually(() => rig.Summaries.Calls.Count == 1, "first summary read");

        rig.Lane.SessionAccessChangedSubject.OnNext("x");
        rig.Summaries.Answer(0, Summary("x", title: "before"));
        await Eventually(() => rig.Summaries.Calls.Count == 2, "re-issued summary read");
        await Assert.That(pin.IsCompleted).IsFalse();
        rig.Summaries.Answer(1, Summary("x", title: "after"));

        await Assert.That(await pin).IsEqualTo(PinOutcome.Pinned);
        await Assert.That(rig.Latest.Pinned?.Title).IsEqualTo("after");
    }

    [Test]
    public async Task A_newer_ended_refresh_is_not_overwritten_by_an_older_active_one() {
        using var rig = new Rig();
        rig.Connect();
        await rig.PinUnlistedAsync("x", Summary("x"));

        rig.Push(SessionPushKind.ActiveChanged, "x");
        await Eventually(() => rig.Summaries.Calls.Count == 2, "first refresh");
        rig.Push(SessionPushKind.ActiveRemoved, "x");
        await Eventually(() => rig.Summaries.Calls.Count == 3, "second refresh");

        rig.Summaries.Answer(2, Summary("x", "ended"));
        await Eventually(() => rig.Latest.Pinned?.Status == "ended", "newer refresh published");
        rig.Summaries.Answer(1, Summary("x", "active"));
        await Task.Delay(100);

        await Assert.That(rig.Latest.Pinned?.Status).IsEqualTo("ended");
    }

    [Test]
    public async Task Title_change_push_refreshes_the_pin() {
        using var rig = new Rig();
        rig.Connect();
        await rig.PinUnlistedAsync("x", Summary("x", title: "old"));

        rig.Push(SessionPushKind.TitleChanged, "x");
        await Eventually(() => rig.Summaries.Calls.Count == 2, "refresh");
        rig.Summaries.Answer(1, Summary("x", title: "renamed"));

        await Eventually(() => rig.Latest.Pinned?.Title == "renamed", "new title published");
    }

    [Test]
    public async Task Unavailable_refresh_drops_the_session_and_keeps_the_pin_id() {
        using var rig = new Rig();
        var dropped = ImmutableList<string>.Empty;
        using var sub = rig.Service.Dropped.Subscribe(id => ImmutableInterlocked.Update(ref dropped, l => l.Add(id)));
        rig.Connect();
        await rig.PinUnlistedAsync("x", Summary("x"));

        rig.Push(SessionPushKind.ActiveChanged, "x");
        await Eventually(() => rig.Summaries.Calls.Count == 2, "refresh");
        rig.Summaries.Fail(1, ServerReadOutcome.Unavailable);

        await Eventually(() => dropped.Contains("x"), "drop announced");
        await Assert.That(rig.Latest.Pinned).IsNull();
        await Assert.That(rig.Latest.PinnedSessionId).IsEqualTo("x");
        await Assert.That(rig.Service.PinnedSessionId).IsEqualTo("x");
    }

    [Test]
    public async Task Listing_row_wins_while_present_and_the_pin_rehydrates_after_it_leaves() {
        using var rig = new Rig();
        var listed = Row("x", owner: "teammate");
        rig.Listing.Set("ended", "me", listed);
        rig.Connect();
        await Eventually(() => rig.Service.Ended.Count == 1, "listed");
        await Assert.That(await rig.Service.PinAsync("x", Presentation, CancellationToken.None)).IsEqualTo(PinOutcome.Pinned);
        await Assert.That(rig.Latest.Pinned).IsNull();
        var before = rig.Snapshots.Count;

        rig.Listing.Set("ended", "me");
        await rig.Service.ReconcileAsync();

        var dropped = rig.Snapshots.Skip(before).First(s => s.Ended.All(r => r.SessionId != "x"));
        await Assert.That(dropped.Pinned).IsEqualTo(listed);
        await Eventually(() => rig.Summaries.Calls.Count == 1, "rehydrating read");
        rig.Summaries.Answer(0, Summary("x", "ended", title: "fresh"));
        await Eventually(() => rig.Latest.Pinned?.Title == "fresh", "summary row published");
        await Assert.That(rig.Summaries.Calls.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Unpin_then_late_summary_publishes_nothing() {
        using var rig = new Rig();
        rig.Connect();
        var pin = rig.Service.PinAsync("x", Presentation, CancellationToken.None);
        await Eventually(() => rig.Summaries.Calls.Count == 1, "summary read");

        rig.Service.Unpin();
        rig.Summaries.Answer(0, Summary("x"));

        await Assert.That(await pin).IsEqualTo(PinOutcome.Superseded);
        await Assert.That(rig.Latest.Pinned).IsNull();
        await Assert.That(rig.Service.PinnedSessionId).IsNull();
    }

    [Test]
    public async Task Identity_advances_account_on_subject_change_and_generation_on_reconnect() {
        using var rig = new Rig();
        var seen = ImmutableList<SessionIdentity>.Empty;
        using var sub = rig.Service.Identity.Subscribe(i => ImmutableInterlocked.Update(ref seen, l => l.Add(i)));
        await Assert.That(seen[^1].Account).IsEqualTo(0);
        await Assert.That(seen[^1].Connected).IsFalse();

        rig.Connect("u1", epoch: 1);
        var first = seen[^1];
        await Assert.That(first.Connected).IsTrue();

        rig.Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying));
        await Assert.That(seen[^1].Connected).IsFalse();
        rig.Connect("u1", epoch: 2);
        var reconnected = seen[^1];
        await Assert.That(reconnected.Account).IsEqualTo(first.Account);
        await Assert.That(reconnected.Generation).IsGreaterThan(first.Generation);
        await Assert.That(reconnected.Connected).IsTrue();

        rig.Connect("u2", epoch: 3);
        var other = seen[^1];
        await Assert.That(other.Account).IsEqualTo(first.Account + 1);
        await Assert.That(other.Generation).IsGreaterThan(reconnected.Generation);
    }
}
