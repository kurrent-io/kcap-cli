using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Capacitor.App.Services;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

[NotInParallel(nameof(HubTestHost))]
public class ServerConnectionServiceTests {
    static ServerConnectionService Lane(HubTestHost host, string? token = null) =>
        new(host.Url, () => Task.FromResult(token));

    static async Task<T> Next<T>(IObservable<T> source, Func<T, bool> match, int seconds = 10) =>
        await source.Where(match).Take(1).ToTask().WaitAsync(TimeSpan.FromSeconds(seconds));

    [Test]
    public async Task ConnectsAndServesDaemons() {
        await using var host = await HubTestHost.StartAsync();
        HubTestHost.DaemonsHandler = () => [new DaemonInfo { Name = "work-mac", Connected = true }];
        await using var lane = Lane(host);
        lane.Start();

        await Next(lane.Status, s => s.State == ServerLaneState.Connected);
        var daemons = await lane.GetConnectedDaemonsAsync(CancellationToken.None);
        await Assert.That(daemons![0].Name).IsEqualTo("work-mac");
    }

    [Test]
    public async Task BroadcastsSurfaceAsObservables() {
        await using var host = await HubTestHost.StartAsync();
        await using var lane = Lane(host);
        lane.Start();
        await Next(lane.Status, s => s.State == ServerLaneState.Connected);

        var agentsPing = lane.AgentInstancesChanged.Take(1).ToTask();
        var failure    = lane.LaunchFailures.Take(1).ToTask();
        await host.BroadcastAsync(HubBroadcasts.AgentInstancesChanged);
        await host.BroadcastAsync(HubBroadcasts.LaunchFailed, "a1", "launch_denied_by_owner: default");
        await agentsPing.WaitAsync(TimeSpan.FromSeconds(10));
        var f = await failure.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(f.AgentId).IsEqualTo("a1");
        await Assert.That(f.Reason).Contains("launch_denied_by_owner");
    }

    [Test]
    public async Task NoServerMeansDormantForever() {
        await using var lane = new ServerConnectionService(serverUrl: null, () => Task.FromResult<string?>(null));
        lane.Start();
        var status = await lane.Status.Take(1).ToTask();
        await Assert.That(status.State).IsEqualTo(ServerLaneState.Dormant);
        await Assert.That(await lane.GetConnectedDaemonsAsync(CancellationToken.None)).IsNull();
    }

    [Test]
    public async Task ColdStartFailureRetriesUntilServerAppears() {
        // Pins only: a cold connect against a dead URL (a started-then-stopped host, so the URL
        // is real but nothing answers) settles on Retrying, never throwing out of RunAsync.
        await using var host = await HubTestHost.StartAsync();
        var url = host.Url;
        await host.StopAsync();

        await using var lane = new ServerConnectionService(url, () => Task.FromResult<string?>(null));
        lane.Start();
        await Next(lane.Status, s => s.State == ServerLaneState.Retrying, seconds: 15);
    }

    [Test]
    public async Task MissingTeamClaimSetsDiagnostic() {
        await using var host = await HubTestHost.StartAsync();
        // "sub" only — no team_id. Header/payload/sig shape per JwtClaimsTests.
        const string token = "eyJhbGciOiJub25lIn0.eyJzdWIiOiJ1MSJ9.s";
        await using var lane = Lane(host, token);
        lane.Start();
        var status = await Next(lane.Status, s => s.State == ServerLaneState.Connected);
        await Assert.That(status.Diagnostic).IsEqualTo(ServerConnectionService.TeamClaimMissingNotice);
    }

    [Test]
    public async Task ConnectedThenServerClosesSurfacesRetrying() {
        await using var host = await HubTestHost.StartAsync();
        await using var lane = Lane(host);
        lane.Start();
        await Next(lane.Status, s => s.State == ServerLaneState.Connected);

        await host.StopAsync();
        await Next(lane.Status, s => s.State == ServerLaneState.Retrying, seconds: 15);
    }

    [Test]
    public async Task RestartReconnects() {
        await using var host = await HubTestHost.StartAsync();
        await using var lane = Lane(host);
        lane.Start();
        await Next(lane.Status, s => s.State == ServerLaneState.Connected);
        await lane.RestartAsync();
        await Next(lane.Status, s => s.State == ServerLaneState.Connected);
    }

    [Test]
    public async Task LaunchInvokesOverTheSharedConnection() {
        await using var host = await HubTestHost.StartAsync();
        HubTestHost.LaunchHandler = payload => {
            // The payload arrives with the pinned snake_case names whatever the policy does.
            if (!payload.TryGetProperty("daemon_name", out var d) || d.GetString() != "work-mac")
                throw new InvalidOperationException("daemon_name missing");
            return "agent-42";
        };
        await using var lane = Lane(host);
        lane.Start();
        await Next(lane.Status, s => s.State == ServerLaneState.Connected);

        var outcome = await ((ILaunchClient)lane).StartAsync(
            new LaunchRequest("work-mac", "/work/repo", "claude", "do it"), CancellationToken.None);
        await Assert.That(outcome.Started).IsTrue();
        await Assert.That(outcome.AgentId).IsEqualTo("agent-42");
        await Assert.That(HubTestHost.LaunchCalls).IsEqualTo(1);
    }

    [Test]
    public async Task LaunchWhileDisconnectedFailsWithoutThrowing() {
        await using var lane = new ServerConnectionService("http://127.0.0.1:1", () => Task.FromResult<string?>(null));
        lane.Start();
        var outcome = await ((ILaunchClient)lane).StartAsync(
            new LaunchRequest("d", "/r", "claude", null), CancellationToken.None);
        await Assert.That(outcome.Started).IsFalse();
        await Assert.That(outcome.Error).IsNotNull();
    }

    [Test]
    public async Task UnauthorizedNegotiateSurfacesSignedOutAndStaysThere() {
        await using var host = await HubTestHost.StartAsync(requireAuth: true);
        string? token = null;
        await using var lane = new ServerConnectionService(host.Url, () => Task.FromResult(token));
        lane.Start();

        await Next(lane.Status, s => s.State == ServerLaneState.SignedOut);

        // RunAsync's loop exits (never schedules a retry) on SignedOut, so the current value is
        // the final one until RestartAsync runs — nothing further to race against here.
        var latest = await lane.Status.Take(1).ToTask();
        await Assert.That(latest.State).IsEqualTo(ServerLaneState.SignedOut);

        var outcome = await ((ILaunchClient)lane).StartAsync(
            new LaunchRequest("d", "/r", "claude", null), CancellationToken.None);
        await Assert.That(outcome.Started).IsFalse();
        await Assert.That(outcome.Unauthorized).IsTrue();

        token = "some-token";
        await lane.RestartAsync();
        await Next(lane.Status, s => s.State == ServerLaneState.Connected);
    }

    [Test]
    public async Task ParkSignedOutPublishesSignedOutAndStopsTheLoopUntilRestarted() {
        await using var host = await HubTestHost.StartAsync();
        await using var lane = Lane(host);
        lane.Start();
        await Next(lane.Status, s => s.State == ServerLaneState.Connected);

        lane.ParkSignedOut();
        await Next(lane.Status, s => s.State == ServerLaneState.SignedOut);

        var outcome = await ((ILaunchClient)lane).StartAsync(
            new LaunchRequest("d", "/r", "claude", null), CancellationToken.None);
        await Assert.That(outcome.Started).IsFalse();
        await Assert.That(outcome.Unauthorized).IsTrue();

        await lane.RestartAsync();
        await Next(lane.Status, s => s.State == ServerLaneState.Connected);
    }

    [Test]
    public async Task ParkSignedOutWithIfEpochIgnoresAStaleEpochButHonorsTheCurrentOne() {
        await using var host = await HubTestHost.StartAsync();
        await using var lane = Lane(host);
        lane.Start();
        var connected = await Next(lane.Status, s => s.State == ServerLaneState.Connected);
        var staleEpoch = connected.Epoch;

        lane.ParkSignedOut();
        await Next(lane.Status, s => s.State == ServerLaneState.SignedOut);

        await lane.RestartAsync();
        var reconnected = await Next(lane.Status, s => s.State == ServerLaneState.Connected && s.Epoch != staleEpoch);

        lane.ParkSignedOut(staleEpoch); // captured before the park above — must be ignored
        await Task.Delay(200);
        var stillConnected = await lane.Status.Take(1).ToTask();
        await Assert.That(stillConnected.State).IsEqualTo(ServerLaneState.Connected);

        lane.ParkSignedOut(reconnected.Epoch); // the generation actually running — must park
        await Next(lane.Status, s => s.State == ServerLaneState.SignedOut);
    }

    /// Every restart is a new connection generation, so the epoch captured from the Connected the
    /// restart replaced cannot park the lane — even though nothing parked it in between and it is
    /// Connected again, the decision that epoch was made under is about a connection that is gone.
    [Test]
    public async Task ParkWithTheEpochOfAConnectionARestartReplacedIsIgnored() {
        await using var host = await HubTestHost.StartAsync();
        await using var lane = Lane(host);
        lane.Start();
        var first = await Next(lane.Status, s => s.State == ServerLaneState.Connected);

        await lane.RestartAsync();
        var second = await Next(lane.Status, s => s.State == ServerLaneState.Connected && s.Epoch != first.Epoch);

        lane.ParkSignedOut(first.Epoch);
        await Task.Delay(200);
        var stillConnected = await lane.Status.Take(1).ToTask();
        await Assert.That(stillConnected.State).IsEqualTo(ServerLaneState.Connected);

        lane.ParkSignedOut(second.Epoch);
        await Next(lane.Status, s => s.State == ServerLaneState.SignedOut);
    }

    /// A park landing while a restart is between retiring one loop and admitting the next must
    /// stand: the restart admits nothing (the generation it reserved is no longer current), and the
    /// retired loop's remaining publishes — the Connecting a fresh attempt opens with included —
    /// are dropped rather than reviving the lane. DiagnoseAsync's token read observes no
    /// cancellation, so gating it holds the retired loop, and with it the whole handover window,
    /// open for as long as the test needs.
    [Test]
    public async Task ParkWhileARestartAwaitsTheRetiredLoopLeavesTheLaneSignedOut() {
        var attemptCallIndex = 0;
        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await HubTestHost.StartAsync();
        await using var lane = new ServerConnectionService(host.Url, () => {
            var n = Interlocked.Increment(ref attemptCallIndex);
            return n <= 2 ? Task.FromResult<string?>(null) : gate.Task;
        });
        using var attemptReset = lane.Status
            .Where(s => s.State == ServerLaneState.Connecting)
            .Subscribe(_ => Interlocked.Exchange(ref attemptCallIndex, 0));
        lane.Start();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref attemptCallIndex) < 3) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("DiagnoseAsync's token read was never reached");
            await Task.Delay(5);
        }

        var restart = lane.RestartAsync();
        await Task.Delay(100); // it cannot pass the retired loop until the gate below releases it
        lane.ParkSignedOut();
        gate.SetResult(null);
        await restart;

        await Task.Delay(2000); // ample for a re-admitted loop to connect and publish over the park
        var latest = await lane.Status.Take(1).ToTask();
        await Assert.That(latest.State).IsEqualTo(ServerLaneState.SignedOut);
    }

    [Test]
    public async Task ParkSignedOutDuringATransportLossRetryingSequenceStaysParked() {
        await using var host = await HubTestHost.StartAsync();
        await using var lane = Lane(host);
        lane.Start();
        await Next(lane.Status, s => s.State == ServerLaneState.Connected);

        await host.StopAsync(); // triggers SignalR's own automatic-reconnect cycle
        await Next(lane.Status, s => s.State == ServerLaneState.Retrying, seconds: 15);

        lane.ParkSignedOut();
        await Next(lane.Status, s => s.State == ServerLaneState.SignedOut);

        // A further reconnect attempt from the SAME (now epoch-stale) loop, if the guard failed,
        // would overwrite this — give the retry policy's next tick a chance to (wrongly) land.
        await Task.Delay(4000);
        var latest = await lane.Status.Take(1).ToTask();
        await Assert.That(latest.State).IsEqualTo(ServerLaneState.SignedOut);
    }

    // Each connect attempt's token provider calls are: SignalR's own two internal reads
    // (negotiate + transport, resolved fast so hub.StartAsync completes normally), then
    // DiagnoseAsync's own third read, gated so the park below deterministically lands while that
    // continuation is still pending. The per-attempt counter resets on every Connecting so a
    // retry (should one happen under load) re-fast-paths its own first two reads rather than
    // inheriting a stale count from an earlier attempt.
    [Test]
    public async Task ParkSignedOutDuringAnInFlightConnectDiscardsTheRacingConnectedPublish() {
        var attemptCallIndex = 0;
        var gate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await HubTestHost.StartAsync();
        await using var lane = new ServerConnectionService(host.Url, () => {
            var n = Interlocked.Increment(ref attemptCallIndex);
            return n <= 2 ? Task.FromResult<string?>(null) : gate.Task;
        });
        using var attemptReset = lane.Status
            .Where(s => s.State == ServerLaneState.Connecting)
            .Subscribe(_ => Interlocked.Exchange(ref attemptCallIndex, 0));
        lane.Start();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref attemptCallIndex) < 3) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("DiagnoseAsync's token read was never reached");
            await Task.Delay(5);
        }

        lane.ParkSignedOut();
        gate.SetResult(null); // release DiagnoseAsync — its Connected publish must be discarded

        await Task.Delay(200); // give the racing Connected publish, if any, time to (wrongly) land
        var latest = await lane.Status.Take(1).ToTask();
        await Assert.That(latest.State).IsEqualTo(ServerLaneState.SignedOut);
    }

    [Test]
    public async Task UnauthorizedIsDetectedAnywhereInTheExceptionChain() {
        var wrapped = new InvalidOperationException(
            "outer", new HttpRequestException("401", null, System.Net.HttpStatusCode.Unauthorized));

        await Assert.That(ServerConnectionService.IsUnauthorized(wrapped)).IsTrue();
        await Assert.That(ServerConnectionService.IsUnauthorized(
            new HttpRequestException("403", null, System.Net.HttpStatusCode.Forbidden))).IsFalse();
        await Assert.That(ServerConnectionService.IsUnauthorized(new InvalidOperationException("no http"))).IsFalse();
    }

    [Test]
    public async Task PermissionBroadcastsSurfaceTyped() {
        await using var host = await HubTestHost.StartAsync();
        await using var lane = Lane(host);
        lane.Start();
        await Next(lane.Status, s => s.State == ServerLaneState.Connected);

        var pending = lane.PermissionPending.Take(1).ToTask();
        var responded = lane.PermissionResponded.Take(2).ToList().ToTask();
        var requests = lane.PermissionRequests.Take(2).ToList().ToTask();
        var elicitations = lane.ElicitationRequests.Take(1).ToTask();
        var access = lane.SessionAccessChanged.Take(1).ToTask();

        await host.BroadcastAsync(HubBroadcasts.PermissionPending, "s1");
        await host.BroadcastAsync(HubBroadcasts.PermissionResponded, "s1", "r1");
        await host.BroadcastAsync(HubBroadcasts.PermissionResponded, "s1", null);
        await host.BroadcastAsync(HubBroadcasts.PermissionRequested, "s1", "r1", "Bash", new { command = "ls" }, null);
        await host.BroadcastAsync(HubBroadcasts.PermissionRequested, "s1", "r2", "fs/write", null,
            new[] { new AcpInteractionOption { OptionId = "allow-once", Label = "Allow", Kind = "allow_once" } });
        await host.BroadcastAsync(HubBroadcasts.AcpElicitationRequested, "s1", "q1", "Pick one",
            new[] { new AcpInteractionOption { OptionId = "a", Label = "A", MinSelections = 1, MaxSelections = 1 } }, false);
        await host.BroadcastAsync(HubBroadcasts.SessionAccessChanged, "s1");

        await Assert.That(await pending.WaitAsync(TimeSpan.FromSeconds(10))).IsEqualTo("s1");
        var pings = await responded.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(pings[0].RequestId).IsEqualTo("r1");
        await Assert.That(pings[1].RequestId).IsNull();
        var reqs = await requests.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(reqs[0].ToolInput!.Value.GetProperty("command").GetString()).IsEqualTo("ls");
        await Assert.That(reqs[0].Options).IsNull();
        await Assert.That(reqs[1].Options![0].OptionId).IsEqualTo("allow-once");
        var q = await elicitations.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(q.Prompt).IsEqualTo("Pick one");
        await Assert.That(q.Options[0].MaxSelections).IsEqualTo(1);
        await Assert.That(await access.WaitAsync(TimeSpan.FromSeconds(10))).IsEqualTo("s1");
    }

    /// One option missing its id must not cost the whole push, and no option may reach a card
    /// without one: the malformed array reads as no options, which asks for free text.
    [Test]
    public async Task AnElicitationWithAMalformedOptionSurfacesWithNoOptions() {
        await using var host = await HubTestHost.StartAsync();
        await using var lane = Lane(host);
        lane.Start();
        await Next(lane.Status, s => s.State == ServerLaneState.Connected);

        var elicitations = lane.ElicitationRequests.Take(1).ToTask();
        await host.BroadcastAsync(HubBroadcasts.AcpElicitationRequested, "s1", "q1", "Pick one",
            new object[] { new { option_id = "a", label = "A" }, new { label = "B" } }, false);

        var q = await elicitations.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(q.Prompt).IsEqualTo("Pick one");
        await Assert.That(q.Options).IsEmpty();
    }

    /// An empty option id is offered, not missing: the daemon accepts an empty-string enum value
    /// and resolves by exact id, so dropping it would degrade a selection question into a
    /// free-text card whose answer cannot satisfy that contract.
    [Test]
    public async Task AnElicitationOptionWithAnEmptyIdStillReachesTheCard() {
        await using var host = await HubTestHost.StartAsync();
        await using var lane = Lane(host);
        lane.Start();
        await Next(lane.Status, s => s.State == ServerLaneState.Connected);

        var elicitations = lane.ElicitationRequests.Take(1).ToTask();
        await host.BroadcastAsync(HubBroadcasts.AcpElicitationRequested, "s1", "q1", "Pick one",
            new object[] { new { option_id = "", label = "Default" }, new { option_id = "b", label = "B" } }, false);

        var q = await elicitations.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(q.Options.Select(o => o.OptionId)).IsEquivalentTo(new[] { "", "b" });
    }

    [Test]
    public async Task ToolInputSentAsAJsonStringIsParsed() {
        await using var host = await HubTestHost.StartAsync();
        await using var lane = Lane(host);
        lane.Start();
        await Next(lane.Status, s => s.State == ServerLaneState.Connected);
        var request = lane.PermissionRequests.Take(1).ToTask();
        await host.BroadcastAsync(HubBroadcasts.PermissionRequested, "s1", "r1", "Bash", "{\"command\":\"pwd\"}", null);
        var r = await request.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(r.ToolInput!.Value.GetProperty("command").GetString()).IsEqualTo("pwd");
    }

    [Test]
    public async Task InvokesRouteToTheHubAndClassifyDenial() {
        await using var host = await HubTestHost.StartAsync();
        HubTestHost.ChatSubscribeHandler = sid => sid != "hidden";
        await using var lane = Lane(host);
        lane.Start();
        await Next(lane.Status, s => s.State == ServerLaneState.Connected);

        await Assert.That((await lane.RequestStopAgentAsync("a1", CancellationToken.None)).Result).IsEqualTo(HubCallResult.Ok);
        await Assert.That(HubTestHost.StopCalls).Contains("a1");
        await Assert.That((await lane.SubscribeToChatAsync("s1", CancellationToken.None)).Result).IsEqualTo(HubCallResult.Ok);
        await Assert.That((await lane.SubscribeToChatAsync("hidden", CancellationToken.None)).Result).IsEqualTo(HubCallResult.Denied);
        await Assert.That((await lane.RegisterSessionAccessWatchAsync("hidden", CancellationToken.None)).Result).IsEqualTo(HubCallResult.Denied);
        await Assert.That((await lane.UnsubscribeFromChatAsync("s1", CancellationToken.None)).Result).IsEqualTo(HubCallResult.Ok);
        await Assert.That(HubTestHost.ChatUnsubscribes).Contains("s1");
    }

    [Test]
    public async Task InvokesReportNotConnectedWithoutALiveHub() {
        await using var lane = new ServerConnectionService(serverUrl: null, () => Task.FromResult<string?>(null));
        lane.Start();
        await Assert.That((await lane.RequestStopAgentAsync("a1", CancellationToken.None)).Result).IsEqualTo(HubCallResult.NotConnected);
    }
}
