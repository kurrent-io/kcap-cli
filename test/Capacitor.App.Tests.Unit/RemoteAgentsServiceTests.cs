using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
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
        lane.AgentsChangedSubject.OnNext(System.Reactive.Unit.Default);
        await Task.Delay(100);
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
        var lane = new FakeServerLane();
        using var svc = new RemoteAgentsService(lane, async _ => {
            var n = Interlocked.Increment(ref callCount);
            return n == 1 ? await gate.Task : new RemoteFetch([Agent("a2")]);
        }, TimeSpan.Zero);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1")); // fetch #1 in flight
        await Eventually(() => Volatile.Read(ref callCount) == 1);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u2")); // clear + generation bump

        gate.SetResult(new RemoteFetch([Agent("a1")])); // u1's stale rows land after the identity change

        await Task.Delay(150); // give the stale completion a chance to (wrongly) publish
        await Assert.That(svc.Agents.Count).IsEqualTo(0);

        lane.AgentsChangedSubject.OnNext(System.Reactive.Unit.Default); // a fresh fetch under u2
        await Eventually(() => svc.Agents.Count == 1 && svc.Agents.Lookup("a2").HasValue);
    }

    [Test]
    public async Task ReconnectWithTheSameSubjectDoesNotClearTheCache() {
        var lane = new FakeServerLane();
        using var svc = new RemoteAgentsService(lane, _ => Task.FromResult(new RemoteFetch([Agent("a1")])), TimeSpan.Zero);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1"));
        await Eventually(() => svc.Agents.Count == 1);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1"));
        await Task.Delay(100);
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
        var lane = new FakeServerLane();
        using var svc = new RemoteAgentsService(lane, async _ => {
            var n = Interlocked.Increment(ref callCount);
            return n == 1 ? await gate.Task : new RemoteFetch([Agent("a2")]);
        }, TimeSpan.Zero, onUnauthorized: _ => Interlocked.Increment(ref parkCalls));

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1")); // fetch #1 in flight
        await Eventually(() => Volatile.Read(ref callCount) == 1);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u2")); // generation bump

        gate.SetResult(new RemoteFetch(null, Unauthorized: true)); // u1's stale Unauthorized lands late

        await Task.Delay(150); // give the stale completion a chance to (wrongly) park
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
        var lane = new FakeServerLane();
        using var svc = new RemoteAgentsService(lane, async _ => {
            var n = Interlocked.Increment(ref callCount);
            return n == 1 ? await gate.Task : new RemoteFetch(null, Unauthorized: true);
        }, TimeSpan.Zero, onUnauthorized: _ => Interlocked.Increment(ref parkCalls));

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1")); // fetch #1 in flight
        await Eventually(() => Volatile.Read(ref callCount) == 1);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1")); // same-subject reconnect

        gate.SetResult(new RemoteFetch(null, Unauthorized: true)); // pre-reconnect fetch's stale Unauthorized

        await Task.Delay(150);
        await Assert.That(Volatile.Read(ref parkCalls)).IsEqualTo(0);

        lane.AgentsChangedSubject.OnNext(System.Reactive.Unit.Default); // a fresh post-reconnect fetch
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

    static AgentInstanceDto Titled(string id, string session, string? title) => Agent(id) with { SessionId = session, Title = title, Prompt = "the prompt" };

    static Func<string, string, CancellationToken, Task<SessionRenameResult>> Applies(string shown) =>
        (_, _, _) => Task.FromResult(new SessionRenameResult(true, shown, null));

    static string? TitleOf(RemoteAgentsService svc, string id) => svc.Agents.Lookup(id).Value.Title;

    [Test]
    public async Task Applied_rename_shows_at_once() {
        var lane = new FakeServerLane();
        using var svc = new RemoteAgentsService(
            lane, _ => Task.FromResult(new RemoteFetch([Titled("a1", "s1", "Old")])), TimeSpan.Zero, Applies("New"));
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1"));
        await Eventually(() => svc.Agents.Count == 1);

        var result = await svc.RenameAsync("a1", "s1", "New", CancellationToken.None);

        await Assert.That(result.Applied).IsTrue();
        await Assert.That(TitleOf(svc, "a1")).IsEqualTo("New");
    }

    [Test]
    public async Task A_fetch_started_before_the_rename_keeps_the_overlay() {
        var calls = 0;
        var gate = new TaskCompletionSource<RemoteFetch>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lane = new FakeServerLane();
        using var svc = new RemoteAgentsService(lane, async _ => {
            var n = Interlocked.Increment(ref calls);
            return n == 1 ? new RemoteFetch([Titled("a1", "s1", "Old")]) : await gate.Task;
        }, TimeSpan.Zero, Applies("New"));
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1"));
        await Eventually(() => svc.Agents.Count == 1);
        lane.AgentsChangedSubject.OnNext(System.Reactive.Unit.Default);
        await Eventually(() => Volatile.Read(ref calls) == 2);

        await svc.RenameAsync("a1", "s1", "New", CancellationToken.None);
        gate.SetResult(new RemoteFetch([Titled("a1", "s1", "Old")]));

        await Task.Delay(150);
        await Assert.That(TitleOf(svc, "a1")).IsEqualTo("New");
    }

    [Test]
    public async Task A_later_fetch_with_a_title_wins() {
        var title = "Old";
        var lane = new FakeServerLane();
        using var svc = new RemoteAgentsService(
            lane, _ => Task.FromResult(new RemoteFetch([Titled("a1", "s1", Volatile.Read(ref title))])), TimeSpan.Zero, Applies("New"));
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1"));
        await Eventually(() => svc.Agents.Count == 1);
        await svc.RenameAsync("a1", "s1", "New", CancellationToken.None);

        Volatile.Write(ref title, "Newer elsewhere");
        lane.AgentsChangedSubject.OnNext(System.Reactive.Unit.Default);

        await Eventually(() => TitleOf(svc, "a1") == "Newer elsewhere");
    }

    [Test]
    public async Task A_later_fetch_without_a_title_keeps_the_overlay() {
        var calls = 0;
        var lane = new FakeServerLane();
        using var svc = new RemoteAgentsService(lane, _ => {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new RemoteFetch([Titled("a1", "s1", null)]));
        }, TimeSpan.Zero, Applies("New"));
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1"));
        await Eventually(() => svc.Agents.Count == 1);
        await svc.RenameAsync("a1", "s1", "New", CancellationToken.None);

        lane.AgentsChangedSubject.OnNext(System.Reactive.Unit.Default);
        await Eventually(() => Volatile.Read(ref calls) == 2);
        await Task.Delay(100);

        await Assert.That(TitleOf(svc, "a1")).IsEqualTo("New");
    }

    // The new identity can see an instance with the same session id: the old account's title must not follow it.
    [Test]
    public async Task Identity_change_clears_the_overlay() {
        var calls = 0;
        var lane = new FakeServerLane();
        using var svc = new RemoteAgentsService(lane, _ => {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new RemoteFetch([Titled("a1", "s1", null)]));
        }, TimeSpan.Zero, Applies("New"));
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1"));
        await Eventually(() => svc.Agents.Count == 1);
        await svc.RenameAsync("a1", "s1", "New", CancellationToken.None);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u2"));
        await Eventually(() => svc.Agents.Count == 0);
        lane.AgentsChangedSubject.OnNext(System.Reactive.Unit.Default);
        await Eventually(() => Volatile.Read(ref calls) == 2 && svc.Agents.Count == 1);
        await Task.Delay(100);

        await Assert.That(TitleOf(svc, "a1")).IsNull();
    }

    [Test]
    public async Task A_rename_completing_after_an_identity_change_is_discarded() {
        var calls = 0;
        var renameGate = new TaskCompletionSource<SessionRenameResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lane = new FakeServerLane();
        using var svc = new RemoteAgentsService(lane, _ => {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new RemoteFetch([Titled("a1", "s1", null)]));
        }, TimeSpan.Zero, (_, _, _) => renameGate.Task);
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1"));
        await Eventually(() => svc.Agents.Count == 1);

        var pending = svc.RenameAsync("a1", "s1", "New", CancellationToken.None);
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u2"));
        await Eventually(() => svc.Agents.Count == 0);
        lane.AgentsChangedSubject.OnNext(System.Reactive.Unit.Default);
        await Eventually(() => Volatile.Read(ref calls) == 2 && svc.Agents.Count == 1);
        renameGate.SetResult(new SessionRenameResult(true, "New", null));
        var result = await pending;

        await Assert.That(result.Applied).IsFalse();
        await Assert.That(result.Reason).IsEqualTo(SessionRenameReasons.IdentityMismatch);
        await Assert.That(TitleOf(svc, "a1")).IsNull();
        lane.AgentsChangedSubject.OnNext(System.Reactive.Unit.Default);
        await Eventually(() => Volatile.Read(ref calls) == 3);
        await Task.Delay(100);
        await Assert.That(TitleOf(svc, "a1")).IsNull();
    }
}
