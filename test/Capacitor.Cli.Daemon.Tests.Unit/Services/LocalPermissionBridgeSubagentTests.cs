using System.Net;
using System.Net.Http.Json;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// The subagent relay: a Claude subagent's hook tells the daemon the subagent is alive or gone,
/// and the bridge hands the attributed agent's report, with its stamp, to the orchestrator.
public class LocalPermissionBridgeSubagentTests {
    const string Session = "6ba7b8109dad11d180b400c04fd430c8";

    sealed class Harness : IAsyncDisposable {
        public LocalPermissionBridge Bridge { get; }
        public FakeTimeProvider Time { get; } = new();
        public HttpClient Client { get; } = new() { Timeout = TimeSpan.FromSeconds(30) };
        public List<(string AgentId, string SubagentId, bool Live, long SentAt)> Seen { get; } = [];

        public Harness(string? attributeTo = "agent-1") {
            Bridge = new LocalPermissionBridge(new FakeServerConnection(respond: null), NullLogger<LocalPermissionBridge>.Instance, EphemeralLoopbackPortSource.Instance, time: Time) {
                AttributeHandler = attributeTo is null ? _ => null : _ => new AttributedAgent(attributeTo),
                SubagentHandler  = (id, subagent, live, sentAt) => Seen.Add((id, subagent, live, sentAt)),
            };
        }

        public Task StartAsync() => Bridge.StartAsync(CancellationToken.None);

        public Task<HttpResponseMessage> PostAsync(object body, string vendor = "claude", string? token = null) {
            var baseUrl = token is null ? Bridge.BaseUrl! : $"http://127.0.0.1:{new Uri(Bridge.BaseUrl!).Port}/{token}";
            return Client.PostAsync($"{baseUrl}/{vendor}/subagent", JsonContent.Create(body));
        }

        public async ValueTask DisposeAsync() {
            await WaitUntilIdleAsync(Bridge);
            await Bridge.DisposeAsync();
            Client.Dispose();
        }
    }

    // StopAsync's drain polls the bridge's own clock, which a FakeTimeProvider never advances on
    // its own.
    static async Task WaitUntilIdleAsync(LocalPermissionBridge bridge) {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (bridge.InFlightHandlersForTest != 0) {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"{bridge.InFlightHandlersForTest} handler(s) still in flight");
            await Task.Delay(10);
        }
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeSubagentTests))]
    public async Task A_live_report_reaches_the_handler_with_the_attributed_agent_and_its_stamp() {
        await using var h = new Harness();
        await h.StartAsync();

        var response = await h.PostAsync(new { session_id = Session, agent_id = "agent-1", cwd = "/repo", subagent_id = "sub-1", live = true, sent_at = 1_700_000_000_000L });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(h.Seen.Single()).IsEqualTo(("agent-1", "sub-1", true, 1_700_000_000_000L));
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeSubagentTests))]
    public async Task A_stop_report_relays_live_false() {
        await using var h = new Harness();
        await h.StartAsync();

        var response = await h.PostAsync(new { session_id = Session, agent_id = "agent-1", subagent_id = "sub-1", live = false, sent_at = 1_700_000_000_000L });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(h.Seen.Single()).IsEqualTo(("agent-1", "sub-1", false, 1_700_000_000_000L));
    }

    /// An older hook sends no stamp, and a stamp that is not an integer is no stamp: the bridge's
    /// own clock at arrival keeps such reports in arrival order.
    [Test, NotInParallel(nameof(LocalPermissionBridgeSubagentTests))]
    public async Task A_report_without_a_usable_stamp_is_stamped_on_arrival() {
        await using var h = new Harness();
        await h.StartAsync();
        var arrival = h.Time.GetUtcNow().ToUnixTimeMilliseconds();

        var missing = await h.PostAsync(new { session_id = Session, agent_id = "agent-1", subagent_id = "sub-1", live = true });
        var text    = await h.PostAsync(new { session_id = Session, agent_id = "agent-1", subagent_id = "sub-2", live = true, sent_at = "soon" });

        await Assert.That(missing.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(text.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(h.Seen.Select(s => s.SentAt)).IsEquivalentTo(new[] { arrival, arrival });
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeSubagentTests))]
    public async Task A_report_missing_a_session_a_subagent_id_or_a_verdict_is_a_bad_request() {
        await using var h = new Harness();
        await h.StartAsync();

        var noSession    = await h.PostAsync(new { agent_id = "agent-1", subagent_id = "sub-1", live = true });
        var noSubagent   = await h.PostAsync(new { session_id = Session, agent_id = "agent-1", live = true });
        var noVerdict    = await h.PostAsync(new { session_id = Session, agent_id = "agent-1", subagent_id = "sub-1" });
        var overCapId    = await h.PostAsync(new { session_id = Session, agent_id = "agent-1", subagent_id = new string('x', PermissionWire.MaxAgentIdBytes + 1), live = true });

        await Assert.That(noSession.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(noSubagent.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(noVerdict.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(overCapId.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(h.Seen).IsEmpty();
    }

    /// A report the ladder cannot place is not an error the hook can act on, so it is acknowledged
    /// and dropped rather than refused.
    [Test, NotInParallel(nameof(LocalPermissionBridgeSubagentTests))]
    public async Task An_unattributed_report_is_acknowledged_and_dropped() {
        await using var h = new Harness(attributeTo: null);
        await h.StartAsync();

        var response = await h.PostAsync(new { session_id = Session, subagent_id = "sub-1", live = true, sent_at = 1L });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(h.Seen).IsEmpty();
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeSubagentTests))]
    [Arguments("codex")]
    [Arguments("cursor")]
    public async Task Only_claude_has_a_subagent_route(string vendor) {
        await using var h = new Harness();
        await h.StartAsync();

        var response = await h.PostAsync(new { session_id = Session, agent_id = "agent-1", subagent_id = "sub-1", live = true, sent_at = 1L }, vendor: vendor);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(h.Seen).IsEmpty();
    }

    /// A reviewer's token buys it no say over another session's subagent count: the attribution
    /// ladder trusts the body's own ids, so only the shared token may report one.
    [Test, NotInParallel(nameof(LocalPermissionBridgeSubagentTests))]
    public async Task A_reviewer_token_has_no_route() {
        await using var h = new Harness();
        await h.StartAsync();
        var reviewerUrl = h.Bridge.RegisterReviewerToken(["kcap-review"]);

        var response = await h.Client.PostAsync($"{reviewerUrl}/claude/subagent",
            JsonContent.Create(new { session_id = Session, agent_id = "agent-1", subagent_id = "sub-1", live = true }));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(h.Seen).IsEmpty();
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeSubagentTests))]
    public async Task An_unknown_token_has_no_subagent_route() {
        await using var h = new Harness();
        await h.StartAsync();

        var response = await h.PostAsync(new { session_id = Session, agent_id = "agent-1", subagent_id = "sub-1", live = true, sent_at = 1L }, token: "nope");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(h.Seen).IsEmpty();
    }

    [Test, NotInParallel(nameof(LocalPermissionBridgeSubagentTests))]
    public async Task An_oversized_body_is_refused() {
        await using var h = new Harness();
        await h.StartAsync();

        var response = await h.PostAsync(new { session_id = Session, agent_id = "agent-1", subagent_id = "sub-1", live = true, pad = new string('x', LocalPermissionBridge.MaxPermissionRequestBodyBytes) });

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.RequestEntityTooLarge);
        await Assert.That(h.Seen).IsEmpty();
    }

    /// One FakeTimeProvider for the orchestrator and its clocks: a live report the bridge admitted
    /// early and ran late is dead on both sides of the stamp-retention boundary — released after
    /// 45 s it is overtaken by its own stop, released after 11 min, once the stop's stamp is no
    /// longer honoured, it is dropped as stale before it reaches the clock.
    [Test, NotInParallel(nameof(LocalPermissionBridgeSubagentTests))]
    [Arguments(45)]
    [Arguments(660)]
    public async Task A_live_report_held_across_its_own_stop_stays_dead_however_long_it_was_held(int heldSeconds) {
        var time = new FakeTimeProvider();
        await using var orch   = AgentOrchestratorHarness.BuildOrchestrator(new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(), timeProvider: time);
        var             agent  = orch.SeedAgentForTest("agent-1");
        var             bridge = orch.PermissionBridgeForTest;
        var hold    = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls   = 0;
        bridge.BeforeHandlerRunsForTest = () => {
            if (Interlocked.Increment(ref calls) != 2) return Task.CompletedTask;
            entered.TrySetResult();
            return hold.Task;
        };
        await bridge.StartAsync(CancellationToken.None);
        try {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            Task<HttpResponseMessage> Post(bool live) => client.PostAsync($"{bridge.BaseUrl}/claude/subagent",
                JsonContent.Create(new { session_id = Session, agent_id = "agent-1", subagent_id = "sub-1", live, sent_at = time.GetUtcNow().ToUnixTimeMilliseconds() }));

            (await Post(live: true)).EnsureSuccessStatusCode();
            await Assert.That(agent.ActivityClock.LiveSubagents).IsEqualTo(1);

            time.Advance(TimeSpan.FromSeconds(1));
            var held = Post(live: true);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

            time.Advance(TimeSpan.FromSeconds(1));
            (await Post(live: false)).EnsureSuccessStatusCode();
            await Assert.That(agent.ActivityClock.LiveSubagents).IsEqualTo(0);

            time.Advance(TimeSpan.FromSeconds(heldSeconds));
            hold.SetResult();
            await Assert.That((await held).StatusCode).IsEqualTo(HttpStatusCode.NoContent);
            await Assert.That(agent.ActivityClock.LiveSubagents).IsEqualTo(0);
        } finally {
            await WaitUntilIdleAsync(bridge);
            await bridge.DisposeAsync();
        }
    }
}
