using Capacitor.App.Services;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

public class RemoteAgentsServiceTests {
    static AgentInstanceDto Agent(string id, string status = "Running", string daemon = "work-mac") =>
        new() { AgentId = id, Status = status, DaemonName = daemon, OwnerUserId = "u1", Vendor = "claude" };

    static void Ping(FakeServerLane lane) => lane.AgentsChangedSubject.OnNext(System.Reactive.Unit.Default);

    // A status edge re-enters the refresh on the calling thread, so a fetch in flight is certain to
    // see the rerun before it completes.
    static void Reconnect(FakeServerLane lane, string subject, int epoch) {
        lane.StatusSubject.OnNext(new(ServerLaneState.Retrying));
        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: subject, Epoch: epoch));
    }

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
        var calls = 0;
        var lane = new FakeServerLane();
        using var svc = new RemoteAgentsService(lane, _ => Interlocked.Increment(ref calls) switch {
            1 => Task.FromResult(new RemoteFetch([Agent("a1")])),
            2 => Task.FromResult(new RemoteFetch(null)),
            _ => new TaskCompletionSource<RemoteFetch>().Task,
        }, TimeSpan.Zero);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected));
        await Eventually(() => svc.Agents.Count == 1);
        Ping(lane);
        await Eventually(() => Volatile.Read(ref calls) == 2);
        // Fetches run one at a time, so a third has started only once the second was applied.
        Ping(lane);
        await Eventually(() => Volatile.Read(ref calls) == 3);
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
            return n == 1 ? await gate.Task : await fresh.Task;
        }, TimeSpan.Zero);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1")); // fetch #1 in flight
        await Eventually(() => Volatile.Read(ref callCount) == 1);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u2")); // clear + generation bump

        gate.SetResult(new RemoteFetch([Agent("a1")])); // u1's stale rows land after the identity change

        Ping(lane); // fetch #2 starts only once #1 was handled
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

        // The identity check runs inside OnNext, so a clear would already have happened.
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
        var lane = new FakeServerLane();
        using var svc = new RemoteAgentsService(lane, async _ => {
            var n = Interlocked.Increment(ref callCount);
            return n == 1 ? await gate.Task : new RemoteFetch([Agent("a2")]);
        }, TimeSpan.Zero, onUnauthorized: _ => Interlocked.Increment(ref parkCalls));

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1")); // fetch #1 in flight
        await Eventually(() => Volatile.Read(ref callCount) == 1);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u2")); // generation bump
        Reconnect(lane, "u2", 2); // queues fetch #2 behind #1

        gate.SetResult(new RemoteFetch(null, Unauthorized: true)); // u1's stale Unauthorized lands late

        await Eventually(() => svc.Agents.Lookup("a2").HasValue);
        await Assert.That(Volatile.Read(ref parkCalls)).IsEqualTo(0);
    }

    // A same-subject reconnect still bumps the generation (a fresh connect, even under the same
    // identity, supersedes anything issued before it), so a pre-reconnect fetch's stale
    // Unauthorized never parks — but a fetch issued after the reconnect still does.
    [Test]
    public async Task StaleUnauthorizedAcrossASameSubjectReconnectDoesNotParkButAFreshOneDoes() {
        var parks = new System.Collections.Concurrent.ConcurrentQueue<int>();
        var callCount = 0;
        var gate = new TaskCompletionSource<RemoteFetch>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lane = new FakeServerLane();
        using var svc = new RemoteAgentsService(lane, async _ => {
            var n = Interlocked.Increment(ref callCount);
            return n == 1 ? await gate.Task : new RemoteFetch(null, Unauthorized: true);
        }, TimeSpan.Zero, onUnauthorized: parks.Enqueue);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1", Epoch: 1)); // fetch #1 in flight
        await Eventually(() => Volatile.Read(ref callCount) == 1);

        lane.StatusSubject.OnNext(new(ServerLaneState.Connected, Subject: "u1", Epoch: 2)); // same-subject reconnect
        Reconnect(lane, "u1", 3); // queues fetch #2 behind #1

        gate.SetResult(new RemoteFetch(null, Unauthorized: true)); // pre-reconnect fetch's stale Unauthorized

        // Both completions are handled in order on one loop: a stale park would be queued first.
        await Eventually(() => !parks.IsEmpty);
        await Assert.That(parks.ToArray()).IsEquivalentTo(new[] { 3 });
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
        using var svc = new RemoteAgentsService(lane, rows.Fetch, TimeSpan.Zero, Applies("New"));
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
        using var svc = new RemoteAgentsService(lane, rows.Fetch, TimeSpan.Zero, Applies("New"));
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
        }, TimeSpan.Zero, Applies("New"));
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
        using var svc = new RemoteAgentsService(lane, rows.Fetch, TimeSpan.Zero, Applies("New"));
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
        using var svc = new RemoteAgentsService(lane, rows.Fetch, TimeSpan.Zero, Applies("New"));
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
        using var svc = new RemoteAgentsService(lane, rows.Fetch, TimeSpan.Zero, Applies("New"));
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

    [Test]
    public async Task The_overlay_yields_after_the_hold() {
        var time = new FakeTimeProvider();
        var lane = new FakeServerLane();
        var rows = new Rows(Titled("a1", "s1", "Old"));
        using var svc = new RemoteAgentsService(lane, rows.Fetch, TimeSpan.Zero, Applies("New"), time);
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
        using var svc = new RemoteAgentsService(lane, rows.Fetch, TimeSpan.Zero, Applies("New"));
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
        using var svc = new RemoteAgentsService(lane, rows.Fetch, TimeSpan.Zero, (_, _, _) => renameGate.Task);
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
