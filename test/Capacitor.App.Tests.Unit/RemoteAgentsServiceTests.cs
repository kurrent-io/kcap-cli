using Capacitor.App.Services;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

public class RemoteAgentsServiceTests {
    static AgentInstanceDto Agent(string id, string status = "Running", string daemon = "work-mac") =>
        new() { AgentId = id, Status = status, DaemonName = daemon, OwnerUserId = "u1", Vendor = "claude" };

    static async Task Eventually(Func<bool> condition, int ms = 5000) {
        var deadline = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition()) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(10);
        }
    }

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
            DaemonsHandler = () => Task.FromResult<IReadOnlyList<DaemonInfo>?>([new DaemonInfo { Name = "work-mac", Connected = true }]),
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
}
