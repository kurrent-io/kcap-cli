using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

public class RemoteAgentsServiceTests {
    static AgentInstanceDto Agent(string id, string status = "Running", string daemon = "work-mac") =>
        new() { AgentId = id, Status = status, DaemonName = daemon, OwnerUserId = "u1", Vendor = "claude" };

    static void Ping(FakeServerLane lane) => lane.AgentsChangedSubject.OnNext(System.Reactive.Unit.Default);

    static async Task Eventually(Func<bool> condition, int ms = 5000) {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

    static Task<RemoteFetch> NoAgents(CancellationToken _) => Task.FromResult(new RemoteFetch(null));

    static IDisposable Record<T>(IObservable<T> source, List<T> into) => source.Subscribe(v => { lock (into) into.Add(v); });

    static T[] Seen<T>(List<T> recorded) { lock (recorded) return [.. recorded]; }

    [Test]
    public async Task ConnectSeedsAgentsAndDaemons() {
        var lane = new FakeServerLane {
            DaemonsHandler = () => Task.FromResult<IReadOnlyList<DaemonInfo>?>([new DaemonInfo { Name = "work-mac", Connected = true }]),
        };
        using var svc = new RemoteAgentsService(lane, _ => Task.FromResult(new RemoteFetch([Agent("a1")])), TimeSpan.Zero);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected));
        await Eventually(() => svc.Agents.Count == 1);
        IReadOnlyList<DaemonInfo>? seen = null;
        using var sub = svc.Daemons.Subscribe(d => seen = d);
        await Eventually(() => seen is { Count: 1 });
    }

    [Test]
    public async Task PingRefreshesAndRemovalsPropagate() {
        var results = new Queue<AgentInstanceDto[]?>([ [Agent("a1"), Agent("a2")], [Agent("a2")] ]);
        var lane = new FakeServerLane();
        using var svc = new RemoteAgentsService(
            lane, _ => Task.FromResult(new RemoteFetch(results.Count > 0 ? results.Dequeue() : null)), TimeSpan.Zero);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected));
        await Eventually(() => svc.Agents.Count == 2);
        lane.AgentsChangedSubject.OnNext(System.Reactive.Unit.Default);
        await Eventually(() => svc.Agents.Count == 1 && svc.Agents.Lookup("a2").HasValue);
    }

    [Test]
    public async Task NullFetchLeavesCacheUntouched() {
        var first = true;
        var lane = new FakeServerLane();
        using var svc = new RemoteAgentsService(lane, _ => {
            var r = first ? new[] { Agent("a1") } : null;
            first = false;
            return Task.FromResult(new RemoteFetch(r));
        }, TimeSpan.Zero);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected));
        await Eventually(() => svc.Agents.Count == 1);
        // Status edges refresh synchronously; no throttle timer is involved.
        lane.StatusSubject.OnNext(new(ServerLaneState.Retrying));
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected));
        await Assert.That(svc.Agents.Count).IsEqualTo(1);
    }

    [Test]
    public async Task IdentityChangeClearsCachesEvenWhenTheNewFetchReturnsNull() {
        var returnNull = false;
        var lane = new FakeServerLane {
            DaemonsHandler = () => Task.FromResult<IReadOnlyList<DaemonInfo>?>(
                returnNull ? null : [new DaemonInfo { Name = "work-mac", Connected = true }]),
        };
        using var svc = new RemoteAgentsService(
            lane, _ => Task.FromResult(new RemoteFetch(returnNull ? null : [Agent("a1")])), TimeSpan.Zero);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1"));
        await Eventually(() => svc.Agents.Count == 1);
        IReadOnlyList<DaemonInfo>? seen = null;
        using var sub = svc.Daemons.Subscribe(d => seen = d);
        await Eventually(() => seen is { Count: 1 });

        returnNull = true;
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u2"));

        await Eventually(() => svc.Agents.Count == 0);
        await Eventually(() => seen is { Count: 0 });
    }

    // A fetch started under u1 that is still gated when u2's identity-change clear lands must
    // never repopulate the cache the clear just emptied.
    [Test]
    public async Task StaleFetchCompletingAfterIdentityChangeIsDiscarded() {
        var callCount = 0;
        var gate = new TaskCompletionSource<RemoteFetch>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fresh = new TaskCompletionSource<RemoteFetch>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lane = new FakeServerLane();
        using var svc = new RemoteAgentsService(lane, async _ => {
            var n = Interlocked.Increment(ref callCount);
            return await (n == 1 ? gate.Task : fresh.Task);
        }, TimeSpan.Zero);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1")); // fetch #1 in flight
        await Eventually(() => Volatile.Read(ref callCount) == 1);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u2")); // clear + generation bump

        // The next fetch can start only after the stale result has been processed.
        lane.AgentsChangedSubject.OnNext(System.Reactive.Unit.Default);
        gate.SetResult(new RemoteFetch([Agent("a1")])); // u1's stale rows land after the identity change

        await Eventually(() => Volatile.Read(ref callCount) == 2);
        await Assert.That(svc.Agents.Count).IsEqualTo(0);

        fresh.SetResult(new RemoteFetch([Agent("a2")]));
        await Eventually(() => svc.Agents.Count == 1 && svc.Agents.Lookup("a2").HasValue);
    }

    [Test]
    public async Task ReconnectWithTheSameSubjectDoesNotClearTheCache() {
        var lane = new FakeServerLane();
        using var svc = new RemoteAgentsService(lane, _ => Task.FromResult(new RemoteFetch([Agent("a1")])), TimeSpan.Zero);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1"));
        await Eventually(() => svc.Agents.Count == 1);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1"));
        await Assert.That(svc.Agents.Count).IsEqualTo(1);
    }

    // Drives the coalescing case through repeated Connected/Retrying status edges rather than
    // AgentInstancesChanged pings: the latter go through Throttle, whose real scheduler makes
    // whether a rapid burst reaches RefreshAgentsAsync as one or several attempts a timing race
    // — the same race either way it lands. Status flows through no Throttle, so each edge below
    // deterministically re-enters RefreshAgentsAsync on the calling thread while call #1 still
    // holds the busy flag, exercising the exact contended path the lock-guarded busy/rerun pair
    // exists for.
    [Test]
    public async Task OverlappingRefreshesCoalesceIntoOneTrailingRun() {
        var callCount = 0;
        var gate = new TaskCompletionSource<RemoteFetch>(TaskCreationOptions.RunContinuationsAsynchronously);
        var trailing = new RemoteFetch([Agent("a2")]);
        var lane = new FakeServerLane();
        using var svc = new RemoteAgentsService(lane, async _ => {
            var n = Interlocked.Increment(ref callCount);
            return n == 1 ? await gate.Task : trailing;
        }, TimeSpan.Zero);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected)); // call #1 starts, blocks on gate

        // Six edges while call #1 is still in flight: every one must coalesce into the same
        // pending rerun instead of queuing its own sequential re-fetch.
        for (var i = 0; i < 3; i++) {
            lane.StatusSubject.OnNext(new(ServerLaneState.Retrying));
            lane.StatusSubject.OnNext(new(ServerLaneState.Connected));
        }

        gate.SetResult(new RemoteFetch([Agent("a1")]));

        await Eventually(() => svc.Agents.Lookup("a2").HasValue);
        await Assert.That(Volatile.Read(ref callCount)).IsEqualTo(2);
    }

    // An Unauthorized fetch must invoke onUnauthorized, once per occurrence, without disturbing
    // whatever rows the cache already holds.
    [Test]
    public async Task UnauthorizedFetchInvokesTheCallbackAndLeavesTheCacheUntouched() {
        var unauthorizedCalls = 0;
        var lane = new FakeServerLane();
        var first = true;
        using var svc = new RemoteAgentsService(
            lane,
            _ => {
                var result = first ? new RemoteFetch([Agent("a1")]) : new RemoteFetch(null, Unauthorized: true);
                first = false;
                return Task.FromResult(result);
            },
            TimeSpan.Zero, onUnauthorized: _ => Interlocked.Increment(ref unauthorizedCalls));

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected));
        await Eventually(() => svc.Agents.Count == 1);

        lane.AgentsChangedSubject.OnNext(System.Reactive.Unit.Default);
        await Eventually(() => Volatile.Read(ref unauthorizedCalls) == 1);
        await Assert.That(svc.Agents.Count).IsEqualTo(1);
    }

    // An Unauthorized completion from a fetch issued under the OLD identity must never park the
    // NEW (healthy) lane — the generation bump on the u2 connect makes u1's fetch obsolete for
    // the callback exactly as it already is for the cache publish.
    [Test]
    public async Task StaleUnauthorizedAfterIdentityChangeDoesNotParkTheNewLane() {
        var parkCalls = 0;
        var callCount = 0;
        var gate = new TaskCompletionSource<RemoteFetch>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fresh = new TaskCompletionSource<RemoteFetch>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lane = new FakeServerLane();
        using var svc = new RemoteAgentsService(lane, async _ => {
            var n = Interlocked.Increment(ref callCount);
            return await (n == 1 ? gate.Task : fresh.Task);
        }, TimeSpan.Zero, onUnauthorized: _ => Interlocked.Increment(ref parkCalls));

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1")); // fetch #1 in flight
        await Eventually(() => Volatile.Read(ref callCount) == 1);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u2")); // generation bump

        // The next fetch can start only after the stale result has been processed.
        lane.AgentsChangedSubject.OnNext(System.Reactive.Unit.Default);
        gate.SetResult(new RemoteFetch(null, Unauthorized: true)); // u1's stale Unauthorized lands late

        await Eventually(() => Volatile.Read(ref callCount) == 2);
        fresh.SetResult(new RemoteFetch([Agent("a2")]));
        await Eventually(() => svc.Agents.Count == 1);
        await Assert.That(Volatile.Read(ref parkCalls)).IsEqualTo(0);
    }

    // A same-subject reconnect still bumps the generation (a fresh connect, even under the same
    // identity, supersedes anything issued before it), so a pre-reconnect fetch's stale
    // Unauthorized never parks — but a fetch issued after the reconnect still does.
    [Test]
    public async Task StaleUnauthorizedAcrossASameSubjectReconnectDoesNotParkButAFreshOneDoes() {
        var parkCalls = 0;
        var callCount = 0;
        var gate = new TaskCompletionSource<RemoteFetch>(TaskCreationOptions.RunContinuationsAsynchronously);
        var fresh = new TaskCompletionSource<RemoteFetch>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lane = new FakeServerLane();
        using var svc = new RemoteAgentsService(lane, async _ => {
            var n = Interlocked.Increment(ref callCount);
            return await (n == 1 ? gate.Task : fresh.Task);
        }, TimeSpan.Zero, onUnauthorized: _ => Interlocked.Increment(ref parkCalls));

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1")); // fetch #1 in flight
        await Eventually(() => Volatile.Read(ref callCount) == 1);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1")); // same-subject reconnect

        // The next fetch can start only after the stale result has been processed.
        lane.AgentsChangedSubject.OnNext(System.Reactive.Unit.Default);
        gate.SetResult(new RemoteFetch(null, Unauthorized: true)); // pre-reconnect fetch's stale Unauthorized

        await Eventually(() => Volatile.Read(ref callCount) == 2);
        await Assert.That(Volatile.Read(ref parkCalls)).IsEqualTo(0);

        fresh.SetResult(new RemoteFetch(null, Unauthorized: true));
        await Eventually(() => Volatile.Read(ref parkCalls) == 1);
    }

    // onUnauthorized must carry the lane's OWN Epoch off the Connected the fetch started under —
    // ServerConnectionService.ParkSignedOut(int) re-validates against this at the moment it
    // actually parks, rather than trusting a decision this service already committed to before
    // releasing its own lock.
    [Test]
    public async Task OnUnauthorizedReceivesTheLaneEpochOfTheConnectedTheFetchStartedUnder() {
        int? receivedEpoch = null;
        var lane = new FakeServerLane();
        using var svc = new RemoteAgentsService(
            lane, _ => Task.FromResult(new RemoteFetch(null, Unauthorized: true)), TimeSpan.Zero,
            onUnauthorized: epoch => receivedEpoch = epoch);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Epoch: 7));

        await Eventually(() => receivedEpoch is not null);
        await Assert.That(receivedEpoch).IsEqualTo(7);
    }

    /// When the agents HTTP fetch finishes ahead of GetConnectedDaemonsAsync on Connect, Agents
    /// must wait for Daemons to publish so AgentDirectory never observes remote rows against an
    /// unseeded empty daemon list.
    [Test]
    public async Task ConnectPublishesDaemonsBeforeAgentsWhenAgentsFetchFinishesFirst() {
        var daemonsGate = new TaskCompletionSource<IReadOnlyList<DaemonInfo>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lane = new FakeServerLane { DaemonsHandler = () => daemonsGate.Task };
        using var svc = new RemoteAgentsService(lane, _ => Task.FromResult(new RemoteFetch([Agent("a1")])), TimeSpan.Zero);

        IReadOnlyList<DaemonInfo> seenDaemons = [];
        var published = new TaskCompletionSource<IReadOnlyList<DaemonInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var daemonsSub = svc.Daemons.Subscribe(d => seenDaemons = d);
        using var agentsSub = svc.Agents.Connect().Subscribe(changes => {
            if (changes.Count > 0) published.TrySetResult(seenDaemons);
        });

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected));
        // The synchronous fetch has reached the daemon gate before OnNext returns.
        await Assert.That(svc.Agents.Count).IsEqualTo(0);

        daemonsGate.SetResult([new DaemonInfo { Name = "work-mac", MachineId = "m1", OwnerUserId = "u1", Connected = true }]);

        // Cache count can change before Connect delivers its notification. Await the observer.
        var daemonsWhenAgentsPublished = await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.That(daemonsWhenAgentsPublished.Count).IsEqualTo(1);
        await Assert.That(svc.Agents.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_connect_publishes_an_unanswered_listing_then_the_list() {
        IReadOnlyList<DaemonInfo> rows = [new DaemonInfo { Name = "work-mac", Connected = true }];
        var lane = new FakeServerLane { DaemonsHandler = () => Task.FromResult<IReadOnlyList<DaemonInfo>?>(rows) };
        using var svc = new RemoteAgentsService(lane, NoAgents, TimeSpan.Zero, time: new FakeTimeProvider());
        var listings = new List<DaemonListing>();
        using var sub = Record(svc.Listing, listings);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1"));

        await Eventually(() => Seen(listings).Length == 3);
        await Assert.That(Seen(listings)).IsEquivalentTo(
            [new DaemonListing(null, 0, false), new DaemonListing(null, 1, false), new DaemonListing(rows, 1, false)],
            CollectionOrdering.Matching);
    }

    /// Pins the first two backoff steps (3 s, then 6 s) and that an answer stops the retries.
    [Test]
    public async Task A_failed_fetch_publishes_FetchFailed_and_retries_with_backoff() {
        var time = new FakeTimeProvider();
        var calls = 0;
        IReadOnlyList<DaemonInfo> rows = [new DaemonInfo { Name = "work-mac", Connected = true }];
        var lane = new FakeServerLane {
            DaemonsHandler = () => Interlocked.Increment(ref calls) switch {
                1 => Task.FromResult<IReadOnlyList<DaemonInfo>?>(null),
                2 => throw new InvalidOperationException("hub call failed"),
                _ => Task.FromResult<IReadOnlyList<DaemonInfo>?>(rows),
            },
        };
        using var svc = new RemoteAgentsService(lane, NoAgents, TimeSpan.Zero, time: time);
        var listings = new List<DaemonListing>();
        using var listingSub = Record(svc.Listing, listings);
        var daemons = new List<IReadOnlyList<DaemonInfo>>();
        using var daemonsSub = Record(svc.Daemons, daemons);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1"));
        await Assert.That(Volatile.Read(ref calls)).IsEqualTo(1);
        await Assert.That(Seen(listings)[^1]).IsEqualTo(new DaemonListing(null, 1, true));

        time.Advance(TimeSpan.FromSeconds(3) - TimeSpan.FromTicks(1));
        await Assert.That(Volatile.Read(ref calls)).IsEqualTo(1);
        time.Advance(TimeSpan.FromTicks(1));
        await Eventually(() => Volatile.Read(ref calls) == 2);
        await Eventually(() => Seen(listings).Length == 4);
        await Assert.That(Seen(listings)[^1]).IsEqualTo(new DaemonListing(null, 1, true));

        time.Advance(TimeSpan.FromSeconds(6) - TimeSpan.FromTicks(1));
        await Assert.That(Volatile.Read(ref calls)).IsEqualTo(2);
        time.Advance(TimeSpan.FromTicks(1));
        await Eventually(() => Seen(listings)[^1].Daemons is not null);
        await Assert.That(Seen(listings)[^1]).IsEqualTo(new DaemonListing(rows, 1, false));
        await Assert.That(Seen(daemons).Select(d => d.Count)).IsEquivalentTo([0, 1], CollectionOrdering.Matching);

        time.Advance(TimeSpan.FromMinutes(5));
        await Assert.That(Volatile.Read(ref calls)).IsEqualTo(3);
    }

    [Test]
    public async Task A_reconnect_returns_the_listing_to_unanswered_until_it_answers() {
        var calls = 0;
        IReadOnlyList<DaemonInfo> first = [new DaemonInfo { Name = "work-mac", Connected = true }];
        IReadOnlyList<DaemonInfo> second = [new DaemonInfo { Name = "home-mac", Connected = true }];
        var gate = new TaskCompletionSource<IReadOnlyList<DaemonInfo>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lane = new FakeServerLane {
            DaemonsHandler = () => Interlocked.Increment(ref calls) == 1 ? Task.FromResult<IReadOnlyList<DaemonInfo>?>(first) : gate.Task,
        };
        using var svc = new RemoteAgentsService(lane, NoAgents, TimeSpan.Zero, time: new FakeTimeProvider());
        var listings = new List<DaemonListing>();
        using var sub = Record(svc.Listing, listings);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1"));
        await Assert.That(Seen(listings)[^1]).IsEqualTo(new DaemonListing(first, 1, false));

        lane.StatusSubject.OnNext(new(ServerLaneState.Retrying));
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1"));
        await Assert.That(Volatile.Read(ref calls)).IsEqualTo(2);
        await Assert.That(Seen(listings)[^1]).IsEqualTo(new DaemonListing(null, 2, false));

        gate.SetResult(second);
        await Eventually(() => Seen(listings).Length == 5);
        await Assert.That(Seen(listings)[^1]).IsEqualTo(new DaemonListing(second, 2, false));
    }

    [Test]
    public async Task RefreshDaemonsAsync_fetches_at_once() {
        var time = new FakeTimeProvider();
        var calls = 0;
        IReadOnlyList<DaemonInfo> rows = [new DaemonInfo { Name = "work-mac", Connected = true }];
        var lane = new FakeServerLane {
            DaemonsHandler = () => Task.FromResult<IReadOnlyList<DaemonInfo>?>(Interlocked.Increment(ref calls) == 1 ? null : rows),
        };
        using var svc = new RemoteAgentsService(lane, NoAgents, TimeSpan.Zero, time: time);
        var listings = new List<DaemonListing>();
        using var sub = Record(svc.Listing, listings);
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1"));
        await Assert.That(Seen(listings)[^1]).IsEqualTo(new DaemonListing(null, 1, true));

        await svc.RefreshDaemonsAsync();

        await Assert.That(Volatile.Read(ref calls)).IsEqualTo(2);
        await Assert.That(Seen(listings)[^1]).IsEqualTo(new DaemonListing(rows, 1, false));
        // The answer withdrew the retry the failure had scheduled.
        time.Advance(TimeSpan.FromSeconds(30));
        await Assert.That(Volatile.Read(ref calls)).IsEqualTo(2);
    }

    /// A fetch issued before a reconnect answers after it: neither Listing nor Daemons may carry
    /// that answer, and the coalesced refetch under the new connection is what publishes.
    [Test]
    public async Task A_result_from_a_superseded_generation_is_dropped() {
        var calls = 0;
        IReadOnlyList<DaemonInfo> stale = [new DaemonInfo { Name = "old-mac", Connected = true }];
        IReadOnlyList<DaemonInfo> fresh = [new DaemonInfo { Name = "work-mac", Connected = true }];
        var gate = new TaskCompletionSource<IReadOnlyList<DaemonInfo>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lane = new FakeServerLane {
            DaemonsHandler = () => Interlocked.Increment(ref calls) == 1 ? gate.Task : Task.FromResult<IReadOnlyList<DaemonInfo>?>(fresh),
        };
        using var svc = new RemoteAgentsService(lane, NoAgents, TimeSpan.Zero, time: new FakeTimeProvider());
        var listings = new List<DaemonListing>();
        using var listingSub = Record(svc.Listing, listings);
        var daemons = new List<IReadOnlyList<DaemonInfo>>();
        using var daemonsSub = Record(svc.Daemons, daemons);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1"));
        lane.StatusSubject.OnNext(new(ServerLaneState.Retrying));
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1"));
        gate.SetResult(stale);

        await Eventually(() => Volatile.Read(ref calls) == 2);
        await Eventually(() => Seen(listings)[^1].Daemons is not null);
        await Assert.That(Seen(listings)).IsEquivalentTo(
            [new DaemonListing(null, 0, false), new DaemonListing(null, 1, false), new DaemonListing(null, 2, false),
             new DaemonListing(fresh, 2, false)],
            CollectionOrdering.Matching);
        await Assert.That(Seen(daemons).Any(d => ReferenceEquals(d, stale))).IsFalse();
    }

    /// The local edition connects without a subject, so the listing cannot ride the subject-keyed
    /// generation: a reconnect still reads unanswered, and a fetch from before it never answers.
    [Test]
    public async Task A_reconnect_without_a_subject_returns_the_listing_to_unanswered_and_drops_the_stale_result() {
        var calls = 0;
        IReadOnlyList<DaemonInfo> stale = [new DaemonInfo { Name = "old-mac", Connected = true }];
        IReadOnlyList<DaemonInfo> fresh = [new DaemonInfo { Name = "work-mac", Connected = true }];
        var gate = new TaskCompletionSource<IReadOnlyList<DaemonInfo>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lane = new FakeServerLane {
            DaemonsHandler = () => Interlocked.Increment(ref calls) == 1 ? gate.Task : Task.FromResult<IReadOnlyList<DaemonInfo>?>(fresh),
        };
        using var svc = new RemoteAgentsService(lane, NoAgents, TimeSpan.Zero, time: new FakeTimeProvider());
        var listings = new List<DaemonListing>();
        using var sub = Record(svc.Listing, listings);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected));
        lane.StatusSubject.OnNext(new(ServerLaneState.Retrying));
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected));
        gate.SetResult(stale);

        await Eventually(() => Volatile.Read(ref calls) == 2);
        await Eventually(() => Seen(listings)[^1].Daemons is not null);
        await Assert.That(Seen(listings)).IsEquivalentTo(
            [new DaemonListing(null, 0, false), new DaemonListing(null, 1, false), new DaemonListing(null, 2, false),
             new DaemonListing(fresh, 2, false)],
            CollectionOrdering.Matching);
    }

    /// The lane can publish Connected twice in a row when a second reconnect lands during the
    /// first one's token read. The second resets the listing and supersedes the fetch in flight, so
    /// it must start a fetch of its own, or the listing stays unanswered with no retry pending.
    [Test]
    public async Task A_second_Connected_in_a_row_fetches_again_so_the_listing_and_accounts_answer() {
        var daemonCalls = 0;
        var accountCalls = 0;
        IReadOnlyList<DaemonInfo> stale = [new DaemonInfo { Name = "old-mac", Connected = true }];
        IReadOnlyList<DaemonInfo> fresh = [new DaemonInfo { Name = "work-mac", Connected = true }];
        var daemonsGate = new TaskCompletionSource<IReadOnlyList<DaemonInfo>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var accountsGate = new TaskCompletionSource<IReadOnlyList<DaemonAccountsDto>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lane = new FakeServerLane {
            DaemonsHandler = () => Interlocked.Increment(ref daemonCalls) == 1
                ? daemonsGate.Task : Task.FromResult<IReadOnlyList<DaemonInfo>?>(fresh),
            DaemonAccountsHandler = () => Interlocked.Increment(ref accountCalls) == 1
                ? accountsGate.Task
                : Task.FromResult<IReadOnlyList<DaemonAccountsDto>?>([new() { DaemonName = "work-mac", HostId = "h1", Revision = 2 }]),
        };
        using var svc = new RemoteAgentsService(lane, NoAgents, TimeSpan.Zero, time: new FakeTimeProvider());
        var listings = new List<DaemonListing>();
        using var listingSub = Record(svc.Listing, listings);
        var accounts = new List<IReadOnlyList<DaemonAccountsDto>>();
        using var accountsSub = Record(svc.DaemonAccounts, accounts);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1"));
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1"));
        daemonsGate.SetResult(stale);
        accountsGate.SetResult([new() { DaemonName = "old-mac", HostId = "h0", Revision = 1 }]);

        await Eventually(() => Seen(listings)[^1].Daemons is not null);
        await Eventually(() => Seen(accounts)[^1].Count == 1);
        await Assert.That(Seen(listings)[^1]).IsEqualTo(new DaemonListing(fresh, 2, false));
        await Assert.That(Volatile.Read(ref daemonCalls)).IsEqualTo(2);
        await Assert.That(Seen(accounts)[^1][0].Revision).IsEqualTo(2L);
        // The fresh listing's new incarnation adds a fetch unless it coalesces into the one in flight.
        await Assert.That(Volatile.Read(ref accountCalls)).IsBetween(2, 3);
    }

    [Test]
    public async Task Daemon_accounts_are_seeded_on_connect_refreshed_on_the_push_and_cleared_on_a_subject_change() {
        static DaemonAccountsDto Row(long revision) => new() { DaemonName = "work-mac", HostId = "h1", Revision = revision };
        var calls = 0;
        var lane = new FakeServerLane {
            DaemonAccountsHandler = () => Task.FromResult<IReadOnlyList<DaemonAccountsDto>?>(Interlocked.Increment(ref calls) switch {
                1 => [Row(1)],
                2 => [Row(2)],
                _ => null,
            }),
        };
        using var svc = new RemoteAgentsService(lane, NoAgents, TimeSpan.Zero, time: new FakeTimeProvider());
        var seen = new List<IReadOnlyList<DaemonAccountsDto>>();
        using var sub = Record(svc.DaemonAccounts, seen);
        long[] Revisions() => [.. Seen(seen).Select(l => l.Select(d => d.Revision).SingleOrDefault())];

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1"));
        await Eventually(() => Revisions().Length == 2);
        lane.DaemonAccountsChangedSubject.OnNext(System.Reactive.Unit.Default);
        await Eventually(() => Revisions().Length == 3);
        // A failed fetch keeps the last list.
        lane.DaemonAccountsChangedSubject.OnNext(System.Reactive.Unit.Default);
        await Eventually(() => Volatile.Read(ref calls) == 3);
        lane.StatusSubject.OnNext(new(ServerLaneState.Retrying));
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u2"));
        await Eventually(() => Volatile.Read(ref calls) == 4);

        await Assert.That(Revisions()).IsEquivalentTo([0L, 1L, 2L, 0L], CollectionOrdering.Matching);
    }

    /// Pins the first two backoff steps (3 s, then 6 s) and that an answer stops the retries.
    [Test]
    public async Task A_failed_accounts_fetch_retries_with_backoff() {
        var time = new FakeTimeProvider();
        var calls = 0;
        var lane = new FakeServerLane {
            DaemonAccountsHandler = () => Interlocked.Increment(ref calls) switch {
                1 => Task.FromResult<IReadOnlyList<DaemonAccountsDto>?>(null),
                2 => throw new InvalidOperationException("hub call failed"),
                _ => Task.FromResult<IReadOnlyList<DaemonAccountsDto>?>([new() { DaemonName = "work-mac", HostId = "h1", Revision = 1 }]),
            },
        };
        using var svc = new RemoteAgentsService(lane, NoAgents, TimeSpan.Zero, time: time);
        var accounts = new List<IReadOnlyList<DaemonAccountsDto>>();
        using var sub = Record(svc.DaemonAccounts, accounts);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1"));
        await Assert.That(Volatile.Read(ref calls)).IsEqualTo(1);

        time.Advance(TimeSpan.FromSeconds(3) - TimeSpan.FromTicks(1));
        await Assert.That(Volatile.Read(ref calls)).IsEqualTo(1);
        time.Advance(TimeSpan.FromTicks(1));
        await Eventually(() => Volatile.Read(ref calls) == 2);

        time.Advance(TimeSpan.FromSeconds(6) - TimeSpan.FromTicks(1));
        await Assert.That(Volatile.Read(ref calls)).IsEqualTo(2);
        time.Advance(TimeSpan.FromTicks(1));
        await Eventually(() => Seen(accounts)[^1].Count == 1);
        await Assert.That(Volatile.Read(ref calls)).IsEqualTo(3);

        time.Advance(TimeSpan.FromMinutes(5));
        await Assert.That(Volatile.Read(ref calls)).IsEqualTo(3);
    }

    [Test]
    public async Task RefreshDaemonsAsync_fetches_the_accounts_at_once() {
        var time = new FakeTimeProvider();
        var calls = 0;
        var lane = new FakeServerLane {
            DaemonAccountsHandler = () => Task.FromResult<IReadOnlyList<DaemonAccountsDto>?>(Interlocked.Increment(ref calls) == 1
                ? null : [new() { DaemonName = "work-mac", HostId = "h1", Revision = 1 }]),
        };
        using var svc = new RemoteAgentsService(lane, NoAgents, TimeSpan.Zero, time: time);
        var accounts = new List<IReadOnlyList<DaemonAccountsDto>>();
        using var sub = Record(svc.DaemonAccounts, accounts);
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1"));
        await Assert.That(Volatile.Read(ref calls)).IsEqualTo(1);

        await svc.RefreshDaemonsAsync();

        await Assert.That(Volatile.Read(ref calls)).IsEqualTo(2);
        await Assert.That(Seen(accounts)[^1].Single().HostId).IsEqualTo("h1");
        // The answer withdrew the retry the failure had scheduled.
        time.Advance(TimeSpan.FromSeconds(30));
        await Assert.That(Volatile.Read(ref calls)).IsEqualTo(2);
    }

    [Test]
    public async Task Current_is_true_after_a_successful_fetch_and_false_after_a_failed_one() {
        var lane = new FakeServerLane();
        var answers = new Queue<RemoteFetch>([new RemoteFetch([]), new RemoteFetch(null)]);
        using var svc = new RemoteAgentsService(lane, _ => Task.FromResult(answers.Count > 0 ? answers.Dequeue() : new RemoteFetch(null)), TimeSpan.Zero);
        var seen = new List<bool>();
        using var _ = svc.Current.Subscribe(seen.Add);

        lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: "u1", Epoch: 1));
        await Eventually(() => seen.Contains(true));
        lane.AgentsChangedSubject.OnNext(System.Reactive.Unit.Default);
        await Eventually(() => seen[^1] == false);
    }

    /// A read that outlives its connection reports on a lane that is no longer there.
    [Test]
    public async Task A_read_finishing_after_the_lane_drops_does_not_report_current() {
        var lane = new FakeServerLane();
        var gate = new TaskCompletionSource<RemoteFetch>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var svc = new RemoteAgentsService(lane, _ => gate.Task, TimeSpan.Zero);
        var seen = new List<bool>();
        using var _ = svc.Current.Subscribe(seen.Add);

        lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: "u1", Epoch: 1));
        lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying));
        gate.SetResult(new RemoteFetch([]));

        await Task.Delay(100);
        await Assert.That(seen).DoesNotContain(true);
    }

    /// A read admitted after the lane dropped is as stale as one that outlived it.
    [Test]
    public async Task A_refresh_admitted_after_the_lane_dropped_does_not_report_current() {
        var lane = new FakeServerLane();
        using var svc = new RemoteAgentsService(lane, _ => Task.FromResult(new RemoteFetch([])), TimeSpan.Zero);
        var seen = new List<bool>();
        using var _ = svc.Current.Subscribe(seen.Add);
        lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: "u1", Epoch: 1));
        await Eventually(() => seen.Contains(true));
        lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying));

        lane.AgentsChangedSubject.OnNext(System.Reactive.Unit.Default);

        await Task.Delay(100);
        await Assert.That(seen[^1]).IsFalse();
    }

    [Test]
    public async Task Current_drops_when_the_lane_leaves_connected() {
        var lane = new FakeServerLane();
        using var svc = new RemoteAgentsService(lane, _ => Task.FromResult(new RemoteFetch([])), TimeSpan.Zero);
        var seen = new List<bool>();
        using var _ = svc.Current.Subscribe(seen.Add);

        lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: "u1", Epoch: 1));
        await Eventually(() => seen.Contains(true));
        lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying));
        await Assert.That(seen[^1]).IsFalse();
    }

    internal static readonly DateTime FirstConnect = new(2026, 10, 27, 9, 0, 0, DateTimeKind.Utc);

    internal static DaemonInfo Host(string name, string machine = "m1", DateTime? connectedAt = null) =>
        new() { Name = name, OwnerUserId = "u1", MachineId = machine, ConnectedAt = connectedAt ?? FirstConnect, Connected = true };

    static DaemonAccountsDto AccountsOf(string name) => new() { DaemonName = name, HostId = "h-" + name, Revision = 1 };

    /// A lane whose listing and accounts answers the test swaps between steps; a null answer is a failed fetch.
    internal sealed class GateRig : IDisposable {
        public readonly FakeServerLane Lane = new();
        public readonly RemoteAgentsService Service;
        public volatile IReadOnlyList<DaemonInfo>? Listing;
        public volatile IReadOnlyList<DaemonAccountsDto>? Accounts;
        public volatile Func<Task<IReadOnlyList<DaemonInfo>?>>? ListingOverride;
        public volatile Func<Task<IReadOnlyList<DaemonAccountsDto>?>>? AccountsOverride;
        public readonly FakeTimeProvider Time = new();
        public int ListingCalls;
        public int AccountCalls;
        readonly List<IReadOnlyList<DaemonAccountsDto>> _drawable = [];
        readonly List<IReadOnlyList<DaemonAccountsDto>> _retained = [];
        readonly List<DaemonListing> _listings = [];
        readonly IDisposable _subs;

        public GateRig(params string[] names) {
            Listing = [.. names.Select(n => Host(n))];
            Accounts = [.. names.Select(AccountsOf)];
            Lane.DaemonsHandler = () => {
                Interlocked.Increment(ref ListingCalls);
                return ListingOverride?.Invoke() ?? Task.FromResult<IReadOnlyList<DaemonInfo>?>(Listing);
            };
            Lane.DaemonAccountsHandler = () => {
                Interlocked.Increment(ref AccountCalls);
                return AccountsOverride?.Invoke() ?? Task.FromResult<IReadOnlyList<DaemonAccountsDto>?>(Accounts);
            };
            Service = new RemoteAgentsService(Lane, NoAgents, TimeSpan.Zero, time: Time);
            _subs = new System.Reactive.Disposables.CompositeDisposable(
                Record(Service.DrawableDaemonAccounts, _drawable),
                Record(Service.DaemonAccounts, _retained),
                Record(Service.Listing, _listings));
        }

        public string[] Drawable() => [.. Seen(_drawable)[^1].Select(d => d.DaemonName)];
        public string[] Retained() => [.. Seen(_retained)[^1].Select(d => d.DaemonName)];
        public DaemonListing LastListing() => Seen(_listings)[^1];
        public int ListingsSeen() => Seen(_listings).Length;

        public void Connect() => Lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1"));

        /// Waits until the failed accounts fetch numbered `call` has been handled: only then is its retry
        /// scheduled, so the retry's call proves it.
        public async Task FailedFetchHandledAsync(int call) =>
            await Eventually(() => { Time.Advance(TimeSpan.FromSeconds(30)); return Volatile.Read(ref AccountCalls) > call; });

        /// Pushes DaemonsChanged and waits until that listing has answered.
        public async Task RelistAsync() {
            var before = ListingsSeen();
            Lane.DaemonsChangedSubject.OnNext(System.Reactive.Unit.Default);
            await Eventually(() => ListingsSeen() > before);
        }

        public void Dispose() { _subs.Dispose(); Service.Dispose(); }
    }

    [Test]
    public async Task Accounts_become_drawable_after_listing_then_accounts() {
        var accountsGate = new TaskCompletionSource<IReadOnlyList<DaemonAccountsDto>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var rig = new GateRig("work-mac");
        rig.AccountsOverride = () => accountsGate.Task;

        rig.Connect();
        await Assert.That(rig.LastListing().Daemons).IsNotNull();
        await Assert.That(rig.Drawable()).IsEmpty();

        accountsGate.SetResult(rig.Accounts);
        await Eventually(() => rig.Drawable().Length == 1);
        await Assert.That(rig.Drawable()).IsEquivalentTo(["work-mac"]);
    }

    [Test]
    public async Task Accounts_become_drawable_when_the_accounts_fetch_finishes_before_the_listing() {
        var listingGate = new TaskCompletionSource<IReadOnlyList<DaemonInfo>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAccounts = new TaskCompletionSource<IReadOnlyList<DaemonAccountsDto>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var rig = new GateRig("work-mac");
        rig.ListingOverride = () => listingGate.Task;
        rig.AccountsOverride = () => Volatile.Read(ref rig.AccountCalls) == 1 ? Task.FromResult<IReadOnlyList<DaemonAccountsDto>?>(rig.Accounts) : secondAccounts.Task;

        rig.Connect();
        await Assert.That(rig.Retained()).IsEquivalentTo(["work-mac"]);
        await Assert.That(rig.Drawable()).IsEmpty();

        listingGate.SetResult(rig.Listing);
        await Eventually(() => Volatile.Read(ref rig.AccountCalls) == 2);
        await Assert.That(rig.Drawable()).IsEmpty();

        secondAccounts.SetResult(rig.Accounts);
        await Eventually(() => rig.Drawable().Length == 1);
        await Assert.That(rig.Drawable()).IsEquivalentTo(["work-mac"]);
    }

    [Test]
    public async Task An_accounts_fetch_in_flight_when_the_daemon_appears_does_not_open_its_gate() {
        var listingGate = new TaskCompletionSource<IReadOnlyList<DaemonInfo>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inFlight = new TaskCompletionSource<IReadOnlyList<DaemonAccountsDto>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rerun = new TaskCompletionSource<IReadOnlyList<DaemonAccountsDto>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var rig = new GateRig("work-mac");
        rig.ListingOverride = () => listingGate.Task;
        rig.AccountsOverride = () => Volatile.Read(ref rig.AccountCalls) == 1 ? inFlight.Task : rerun.Task;

        rig.Connect();
        listingGate.SetResult(rig.Listing);
        await Eventually(() => rig.LastListing().Daemons is not null);

        inFlight.SetResult(rig.Accounts);
        await Eventually(() => Volatile.Read(ref rig.AccountCalls) == 2);
        await Assert.That(rig.Retained()).IsEquivalentTo(["work-mac"]);
        await Assert.That(rig.Drawable()).IsEmpty();

        rerun.SetResult(rig.Accounts);
        await Eventually(() => rig.Drawable().Length == 1);
    }

    [Test]
    public async Task Lane_loss_closes_the_gate_but_keeps_the_accounts() {
        using var rig = new GateRig("work-mac");
        rig.Connect();
        await Eventually(() => rig.Drawable().Length == 1);

        rig.Lane.StatusSubject.OnNext(new(ServerLaneState.Retrying));

        await Assert.That(rig.Drawable()).IsEmpty();
        await Assert.That(rig.Retained()).IsEquivalentTo(["work-mac"]);
    }

    [Test]
    public async Task A_daemon_leaving_the_listing_closes_its_gate() {
        using var rig = new GateRig("work-mac", "home-mac");
        rig.Connect();
        await Eventually(() => rig.Drawable().Length == 2);

        rig.Listing = [Host("home-mac")];
        await rig.RelistAsync();

        await Assert.That(rig.Drawable()).IsEquivalentTo(["home-mac"]);
        await Assert.That(rig.Retained()).IsEquivalentTo(["work-mac", "home-mac"], CollectionOrdering.Any);
    }

    /// RelistAsync's wait is only a completion signal because a listing goes out after the drawable it gates.
    [Test]
    public async Task An_observer_of_a_listing_reads_the_drawable_it_gates() {
        using var rig = new GateRig("work-mac");
        rig.Connect();
        await Eventually(() => rig.Drawable().Length == 1);
        string[]? drawableAtListing = null;
        using var probe = rig.Service.Listing.Subscribe(l => {
            if (l.Daemons is []) Volatile.Write(ref drawableAtListing, rig.Drawable());
        });

        rig.Listing = [];
        rig.Lane.DaemonsChangedSubject.OnNext(System.Reactive.Unit.Default);

        await Eventually(() => Volatile.Read(ref drawableAtListing) is not null);
        await Assert.That(Volatile.Read(ref drawableAtListing)!).IsEmpty();
    }

    [Test]
    public async Task Reconnect_closes_the_gate_until_a_fresh_accounts_fetch() {
        var accountsGate = new TaskCompletionSource<IReadOnlyList<DaemonAccountsDto>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var rig = new GateRig("work-mac");
        rig.Connect();
        await Eventually(() => rig.Drawable().Length == 1);

        rig.AccountsOverride = () => accountsGate.Task;
        rig.Lane.StatusSubject.OnNext(new(ServerLaneState.Retrying));
        rig.Connect();
        await Assert.That(rig.LastListing().Daemons).IsNotNull();
        await Assert.That(rig.Drawable()).IsEmpty();
        await Assert.That(rig.Retained()).IsEquivalentTo(["work-mac"]);

        accountsGate.SetResult(rig.Accounts);
        await Eventually(() => rig.Drawable().Length == 1);
    }

    [Test]
    public async Task Leave_and_rejoin_with_a_failed_refetch_stays_closed() {
        using var rig = new GateRig("work-mac");
        rig.Connect();
        await Eventually(() => rig.Drawable().Length == 1);

        rig.Listing = [];
        await rig.RelistAsync();
        await Assert.That(rig.Drawable()).IsEmpty();

        var calls = Volatile.Read(ref rig.AccountCalls);
        rig.Accounts = null;
        rig.Listing = [Host("work-mac")];
        await rig.RelistAsync();

        await rig.FailedFetchHandledAsync(calls + 1);
        await Assert.That(rig.Drawable()).IsEmpty();
        await Assert.That(rig.Retained()).IsEquivalentTo(["work-mac"]);
    }

    [Test]
    public async Task Leave_and_rejoin_reopens_after_a_successful_fetch() {
        using var rig = new GateRig("work-mac");
        rig.Connect();
        await Eventually(() => rig.Drawable().Length == 1);

        rig.Listing = [];
        await rig.RelistAsync();
        await Assert.That(rig.Drawable()).IsEmpty();

        rig.Listing = [Host("work-mac", connectedAt: FirstConnect.AddMinutes(5))];
        await rig.RelistAsync();

        await Eventually(() => rig.Drawable().Length == 1);
        await Assert.That(rig.Drawable()).IsEquivalentTo(["work-mac"]);
    }

    [Test]
    public async Task Another_host_taking_the_name_with_a_failed_refetch_stays_closed() {
        using var rig = new GateRig("work-mac");
        rig.Connect();
        await Eventually(() => rig.Drawable().Length == 1);

        var calls = Volatile.Read(ref rig.AccountCalls);
        rig.Accounts = null;
        rig.Listing = [Host("work-mac", machine: "m2")];
        await rig.RelistAsync();

        await rig.FailedFetchHandledAsync(calls + 1);
        await Assert.That(rig.Drawable()).IsEmpty();
        await Assert.That(rig.Retained()).IsEquivalentTo(["work-mac"]);
    }

    [Test]
    public async Task A_new_incarnation_schedules_an_accounts_fetch() {
        using var rig = new GateRig("work-mac");
        rig.Connect();
        await Eventually(() => rig.Drawable().Length == 1);
        var calls = Volatile.Read(ref rig.AccountCalls);

        await rig.RelistAsync();
        await Assert.That(Volatile.Read(ref rig.AccountCalls)).IsEqualTo(calls);

        rig.Listing = [Host("work-mac", connectedAt: FirstConnect.AddMinutes(5))];
        await rig.RelistAsync();
        await Eventually(() => Volatile.Read(ref rig.AccountCalls) == calls + 1);
        await Eventually(() => rig.Drawable().Length == 1);
    }

    static AgentInstanceDto Titled(string id, string session, string? title) => Agent(id) with { SessionId = session, Title = title, Prompt = "the prompt" };

    static Func<string, string, CancellationToken, Task<SessionRenameResult>> Applies(string shown) =>
        (_, _, _) => Task.FromResult(new SessionRenameResult(true, shown, null));

    static string? TitleOf(RemoteAgentsService svc, string id) => svc.Agents.Lookup(id).Value.Title;

    // Serves whatever Rows holds; a sentinel agent in it shows when that fetch has been applied.
    sealed class Rows(params AgentInstanceDto[] initial) {
        AgentInstanceDto[] _rows = initial;
        public void Set(params AgentInstanceDto[] rows) => Volatile.Write(ref _rows, rows);
        public Task<RemoteFetch> Fetch(CancellationToken _) => Task.FromResult(new RemoteFetch(Volatile.Read(ref _rows)));
    }

    static async Task Serve(FakeServerLane lane, RemoteAgentsService svc, Rows rows, string sentinel, params AgentInstanceDto[] next) {
        rows.Set([.. next, Agent(sentinel)]);
        Ping(lane);
        await Eventually(() => svc.Agents.Lookup(sentinel).HasValue);
    }

    [Test]
    public async Task Applied_rename_shows_at_once() {
        var lane = new FakeServerLane();
        var rows = new Rows(Titled("a1", "s1", "Old"));
        using var svc = new RemoteAgentsService(lane, rows.Fetch, TimeSpan.Zero, postRename: Applies("New"));
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1"));
        await Eventually(() => svc.Agents.Count == 1);

        var result = await svc.RenameAsync("a1", "s1", "New", CancellationToken.None);

        await Assert.That(result.Applied).IsTrue();
        await Assert.That(TitleOf(svc, "a1")).IsEqualTo("New");
    }

    [Test]
    public async Task A_rename_republishes_only_rows_of_that_session() {
        var lane = new FakeServerLane();
        var rows = new Rows(Titled("a1", "s1", "One"), Titled("a2", "s2", "Two"));
        using var svc = new RemoteAgentsService(lane, rows.Fetch, TimeSpan.Zero, postRename: Applies("New"));
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1"));
        await Eventually(() => svc.Agents.Count == 2);

        await svc.RenameAsync("a1", "s2", "New", CancellationToken.None);

        await Assert.That(TitleOf(svc, "a1")).IsEqualTo("One");
        await Assert.That(TitleOf(svc, "a2")).IsEqualTo("New");
    }

    [Test]
    public async Task A_fetch_started_before_the_rename_keeps_the_overlay() {
        var calls = 0;
        var gate = new TaskCompletionSource<RemoteFetch>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lane = new FakeServerLane();
        using var svc = new RemoteAgentsService(lane, async _ => {
            var n = Interlocked.Increment(ref calls);
            return n == 1 ? new RemoteFetch([Titled("a1", "s1", "Old")]) : await gate.Task;
        }, TimeSpan.Zero, postRename: Applies("New"));
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1"));
        await Eventually(() => svc.Agents.Count == 1);
        Ping(lane);
        await Eventually(() => Volatile.Read(ref calls) == 2);

        await svc.RenameAsync("a1", "s1", "New", CancellationToken.None);
        gate.SetResult(new RemoteFetch([Titled("a1", "s1", "Other"), Agent("z")]));

        await Eventually(() => svc.Agents.Lookup("z").HasValue);
        await Assert.That(TitleOf(svc, "a1")).IsEqualTo("New");
    }

    [Test]
    public async Task A_later_fetch_with_a_title_wins() {
        var lane = new FakeServerLane();
        var rows = new Rows(Titled("a1", "s1", "Old"));
        using var svc = new RemoteAgentsService(lane, rows.Fetch, TimeSpan.Zero, postRename: Applies("New"));
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1"));
        await Eventually(() => svc.Agents.Count == 1);
        await svc.RenameAsync("a1", "s1", "New", CancellationToken.None);

        await Serve(lane, svc, rows, "z", Titled("a1", "s1", "Newer elsewhere"));

        await Assert.That(TitleOf(svc, "a1")).IsEqualTo("Newer elsewhere");
    }

    [Test]
    public async Task A_later_fetch_without_a_title_keeps_the_overlay() {
        var lane = new FakeServerLane();
        var rows = new Rows(Titled("a1", "s1", null));
        using var svc = new RemoteAgentsService(lane, rows.Fetch, TimeSpan.Zero, postRename: Applies("New"));
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1"));
        await Eventually(() => svc.Agents.Count == 1);
        await svc.RenameAsync("a1", "s1", "New", CancellationToken.None);

        await Serve(lane, svc, rows, "z", Titled("a1", "s1", null));

        await Assert.That(TitleOf(svc, "a1")).IsEqualTo("New");
    }

    // The server's title sync lags the rename write, so a fetch can still carry the replaced title.
    [Test]
    [Arguments("New")]
    [Arguments("Other")]
    public async Task A_later_fetch_still_carrying_the_old_title_keeps_the_overlay(string settled) {
        var lane = new FakeServerLane();
        var rows = new Rows(Titled("a1", "s1", "Old"));
        using var svc = new RemoteAgentsService(lane, rows.Fetch, TimeSpan.Zero, postRename: Applies("New"));
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1"));
        await Eventually(() => svc.Agents.Count == 1);
        await svc.RenameAsync("a1", "s1", "New", CancellationToken.None);

        await Serve(lane, svc, rows, "z1", Titled("a1", "s1", "Old"));
        await Assert.That(TitleOf(svc, "a1")).IsEqualTo("New");

        await Serve(lane, svc, rows, "z2", Titled("a1", "s1", settled));
        await Assert.That(TitleOf(svc, "a1")).IsEqualTo(settled);

        // The entry is gone: a title-less row now shows the server's absence, not the overlay.
        await Serve(lane, svc, rows, "z3", Titled("a1", "s1", null));
        await Assert.That(TitleOf(svc, "a1")).IsNull();
    }

    // Every title an unsettled rename replaced is a lagging read, not just the one before the first rename.
    [Test]
    public async Task A_lagging_fetch_carrying_any_earlier_title_keeps_the_latest_rename() {
        var lane = new FakeServerLane();
        var rows = new Rows(Titled("a1", "s1", "Old"));
        using var svc = new RemoteAgentsService(lane, rows.Fetch, TimeSpan.Zero,
            postRename: (_, title, _) => Task.FromResult(new SessionRenameResult(true, title, null)));
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1"));
        await Eventually(() => svc.Agents.Count == 1);
        await svc.RenameAsync("a1", "s1", "First", CancellationToken.None);
        await svc.RenameAsync("a1", "s1", "Second", CancellationToken.None);

        await Serve(lane, svc, rows, "z1", Titled("a1", "s1", "First"));
        await Assert.That(TitleOf(svc, "a1")).IsEqualTo("Second");
        await Serve(lane, svc, rows, "z2", Titled("a1", "s1", "Old"));
        await Assert.That(TitleOf(svc, "a1")).IsEqualTo("Second");

        await Serve(lane, svc, rows, "z3", Titled("a1", "s1", "Second"));
        await Serve(lane, svc, rows, "z4", Titled("a1", "s1", "First"));
        await Assert.That(TitleOf(svc, "a1")).IsEqualTo("First");
    }

    // Two instances of one session: one without a title has not shown the rename, so both keep it.
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_rename_holds_while_any_instance_of_the_session_has_no_title(bool untitledFirst) {
        var lane = new FakeServerLane();
        var rows = new Rows(Titled("a1", "s1", "Old"), Titled("a2", "s1", "Old"));
        using var svc = new RemoteAgentsService(lane, rows.Fetch, TimeSpan.Zero, postRename: Applies("New"));
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1"));
        await Eventually(() => svc.Agents.Count == 2);
        await svc.RenameAsync("a1", "s1", "New", CancellationToken.None);

        AgentInstanceDto[] pair = untitledFirst
            ? [Titled("a1", "s1", null), Titled("a2", "s1", "New")]
            : [Titled("a1", "s1", "New"), Titled("a2", "s1", null)];
        await Serve(lane, svc, rows, "z1", pair);
        await Assert.That(TitleOf(svc, "a1")).IsEqualTo("New");
        await Assert.That(TitleOf(svc, "a2")).IsEqualTo("New");

        await Serve(lane, svc, rows, "z2", Titled("a1", "s1", null), Titled("a2", "s1", null));
        await Assert.That(TitleOf(svc, "a1")).IsEqualTo("New");
        await Assert.That(TitleOf(svc, "a2")).IsEqualTo("New");
    }

    [Test]
    public async Task The_overlay_yields_after_the_hold() {
        var time = new FakeTimeProvider();
        var lane = new FakeServerLane();
        var rows = new Rows(Titled("a1", "s1", "Old"));
        using var svc = new RemoteAgentsService(lane, rows.Fetch, TimeSpan.Zero, time: time, postRename: Applies("New"));
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1"));
        await Eventually(() => svc.Agents.Count == 1);
        await svc.RenameAsync("a1", "s1", "New", CancellationToken.None);

        time.Advance(TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(1));
        await Serve(lane, svc, rows, "z1", Titled("a1", "s1", "Old"));
        await Assert.That(TitleOf(svc, "a1")).IsEqualTo("New");

        time.Advance(TimeSpan.FromSeconds(1));
        await Serve(lane, svc, rows, "z2", Titled("a1", "s1", "Old"));
        await Assert.That(TitleOf(svc, "a1")).IsEqualTo("Old");
    }

    // The new identity can see an instance with the same session id: the old account's title must not follow it.
    [Test]
    public async Task Identity_change_clears_the_overlay() {
        var lane = new FakeServerLane();
        var rows = new Rows(Titled("a1", "s1", null));
        using var svc = new RemoteAgentsService(lane, rows.Fetch, TimeSpan.Zero, postRename: Applies("New"));
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1"));
        await Eventually(() => svc.Agents.Count == 1);
        await svc.RenameAsync("a1", "s1", "New", CancellationToken.None);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u2"));
        await Assert.That(svc.Agents.Count).IsEqualTo(0);
        await Serve(lane, svc, rows, "z", Titled("a1", "s1", null));

        await Assert.That(TitleOf(svc, "a1")).IsNull();
    }

    [Test]
    public async Task A_rename_completing_after_an_identity_change_is_discarded() {
        var renameGate = new TaskCompletionSource<SessionRenameResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lane = new FakeServerLane();
        var rows = new Rows(Titled("a1", "s1", null));
        using var svc = new RemoteAgentsService(lane, rows.Fetch, TimeSpan.Zero, postRename: (_, _, _) => renameGate.Task);
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1"));
        await Eventually(() => svc.Agents.Count == 1);

        var pending = svc.RenameAsync("a1", "s1", "New", CancellationToken.None);
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u2"));
        await Serve(lane, svc, rows, "z1", Titled("a1", "s1", null));
        renameGate.SetResult(new SessionRenameResult(true, "New", null));
        var result = await pending;

        await Assert.That(result.Applied).IsFalse();
        await Assert.That(result.Reason).IsEqualTo(SessionRenameReasons.IdentityMismatch);
        await Assert.That(TitleOf(svc, "a1")).IsNull();
        await Serve(lane, svc, rows, "z2", Titled("a1", "s1", null));
        await Assert.That(TitleOf(svc, "a1")).IsNull();
    }
}
