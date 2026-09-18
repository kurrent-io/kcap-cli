using System.Net;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Microsoft.Extensions.Time.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Tests.Unit.Commands.Harness;

/// The subagent relay a daemon-hosted Claude session sends its daemon: every hook a subagent runs
/// reports it alive, its stop reports it gone, and neither touches the parent's wait. Bare
/// <c>[NotInParallel]</c> for the same reason as <see cref="ClaudeHookInputWaitRelayTests"/>: the
/// relay drops its own POST once <see cref="DaemonBridgeRelay.Cap"/> is spent and says nothing.
public class ClaudeHookSubagentRelayTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Sid        = "9dc2775376454e4691ecc2d69973c152";
    const string SubagentId = "3f2504e04f8911d39a0c0305e82c3301";

    /// Answers every server post with 200 so the hook reaches its normal end.
    sealed class OkHandler : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    static HookClock Aged(TimeSpan elapsed) {
        var time  = new FakeTimeProvider();
        var clock = new HookClock(time);
        time.Advance(elapsed);
        return clock;
    }

    static WireMockServer Bridge() {
        var bridge = WireMockServer.Start();
        bridge.Given(Request.Create().WithPath("/tok/claude/subagent").UsingPost()).RespondWith(Response.Create().WithStatusCode(204));
        bridge.Given(Request.Create().WithPath("/tok/claude/input-wait").UsingPost()).RespondWith(Response.Create().WithStatusCode(204));
        return bridge;
    }

    static HostedAgent HostedOn(WireMockServer bridge) =>
        new("agent-1", IsRendered: false, new DaemonBridge.Loopback($"http://127.0.0.1:{bridge.Ports[0]}/tok"));

    static JsonNode? Relayed(WireMockServer bridge, string path) =>
        bridge.LogEntries.Where(e => e.RequestMessage.Path == path).Select(e => JsonNode.Parse(e.RequestMessage.Body!)).SingleOrDefault();

    /// The relay rides ahead of client creation, so it is exercised through HandleWithDeps and
    /// asserted on the bridge itself, never on the server.
    async Task<int> RunAsync(HostedAgent hosted, string eventName, HookClock? clock = null, string? agentId = SubagentId) {
        using var client = new HttpClient(new OkHandler());
        var agentField = agentId is null ? "" : $",\"agent_id\":\"{agentId}\"";
        var payload = $$$"""{"hook_event_name":"{{{eventName}}}","session_id":"{{{Sid}}}","cwd":"/tmp","tool_name":"Bash","tool_input":{"command":"ls"}{{{agentField}}}}""";
        return await new ClaudeHookCommand(Config.Root, Resolutions.At("http://server.example", Config.Root), clock ?? new HookClock(TimeProvider.System), Home, TestHarnesses.Under(Home), hosted, new FixedCapacitorHttpClient(), TestWatchers.For(Config.Root, Resolutions.At("http://server.example", Config.Root), new FixedCapacitorHttpClient()), SystemProcessStarter.Instance, router: new GitProviderRouter(), workdir: new WorkingDirectory(AppContext.BaseDirectory))
            .HandleWithDeps(new HookSpool(Config.Root, time: TimeProvider.System), new StringReader(payload), () => Task.FromResult(new AuthAttempt(client, AuthStatus.Ok, null, null)), new StringWriter());
    }

    [Test, NotInParallel]
    [Arguments("SubagentStart")]
    [Arguments("PreToolUse")]
    public async Task A_subagents_hook_reports_it_alive_with_its_stamp(string eventName) {
        using var bridge = Bridge();
        var time = new FakeTimeProvider();

        var exit = await RunAsync(HostedOn(bridge), eventName, new HookClock(time));

        await Assert.That(exit).IsEqualTo(0);
        var body = Relayed(bridge, "/tok/claude/subagent")!;
        await Assert.That(body["live"]!.GetValue<bool>()).IsTrue();
        await Assert.That(body["subagent_id"]!.GetValue<string>()).IsEqualTo(SubagentId);
        await Assert.That(body["agent_id"]!.GetValue<string>()).IsEqualTo("agent-1");
        await Assert.That(body["session_id"]!.GetValue<string>()).IsEqualTo(Sid);
        await Assert.That(body["cwd"]!.GetValue<string>()).IsEqualTo("/tmp");
        await Assert.That(body["sent_at"]!.GetValue<long>()).IsEqualTo(time.GetUtcNow().ToUnixTimeMilliseconds());
        await Assert.That(Relayed(bridge, "/tok/claude/input-wait")).IsNull();
    }

    [Test, NotInParallel]
    public async Task A_subagents_stop_reports_it_gone() {
        using var bridge = Bridge();

        var exit = await RunAsync(HostedOn(bridge), "SubagentStop");

        await Assert.That(exit).IsEqualTo(0);
        var body = Relayed(bridge, "/tok/claude/subagent")!;
        await Assert.That(body["live"]!.GetValue<bool>()).IsFalse();
        await Assert.That(body["subagent_id"]!.GetValue<string>()).IsEqualTo(SubagentId);
        await Assert.That(Relayed(bridge, "/tok/claude/input-wait")).IsNull();
    }

    [Test, NotInParallel]
    public async Task A_parents_hook_reports_no_subagent_and_still_relays_its_wait() {
        using var bridge = Bridge();

        var exit = await RunAsync(HostedOn(bridge), "Stop", agentId: null);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Relayed(bridge, "/tok/claude/subagent")).IsNull();
        await Assert.That(Relayed(bridge, "/tok/claude/input-wait")!["waiting"]!.GetValue<bool>()).IsTrue();
    }

    /// Without the hosted agent id nothing identifies the session to a daemon.
    [Test, NotInParallel]
    public async Task An_unhosted_subagent_hook_relays_nothing() {
        using var bridge = Bridge();
        var unhosted = new HostedAgent(null, IsRendered: false, new DaemonBridge.Loopback($"http://127.0.0.1:{bridge.Ports[0]}/tok"));

        await RunAsync(unhosted, "SubagentStart");

        await Assert.That(bridge.LogEntries.Count).IsEqualTo(0);
    }

    [Test, NotInParallel]
    public async Task An_exhausted_hook_budget_skips_the_relay() {
        using var bridge = Bridge();

        var exit = await RunAsync(HostedOn(bridge), "SubagentStart", Aged(TimeSpan.FromSeconds(10)));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(bridge.LogEntries.Count).IsEqualTo(0);
    }
}
