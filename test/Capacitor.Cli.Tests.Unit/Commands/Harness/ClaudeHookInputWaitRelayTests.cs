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

namespace Capacitor.Cli.Tests.Unit.Commands.Harness;

/// The input-wait relay a daemon-hosted Claude session sends its daemon: which turn boundaries
/// reach it, and what silences it. Bare <c>[NotInParallel]</c> because the relay drops its own POST
/// once <see cref="DaemonInputWaitRelay.Cap"/> is spent and says nothing — on a saturated runner
/// that is the whole second, and a test asserting the POST landed fails for the runner's reasons.
public class ClaudeHookInputWaitRelayTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Sid = "9dc2775376454e4691ecc2d69973c152";

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

    /// The relay rides ahead of client creation, so it is exercised through HandleWithDeps and
    /// asserted on the bridge itself, never on the server.
    async Task<int> RunAsync(HostedAgent hosted, string eventName, HookClock? clock = null, string extraFields = "") {
        using var client = new HttpClient(new OkHandler());
        var payload = $$$"""{"hook_event_name":"{{{eventName}}}","session_id":"{{{Sid}}}","cwd":"/tmp","tool_name":"Bash","tool_input":{"command":"ls"}{{{extraFields}}}}""";
        return await new ClaudeHookCommand(Config.Root, Resolutions.At("http://server.example", Config.Root), clock ?? new HookClock(TimeProvider.System), Home, TestHarnesses.Under(Home), hosted, new FixedCapacitorHttpClient())
            .HandleWithDeps(new HookSpool(Config.Root), new StringReader(payload), () => Task.FromResult(new AuthAttempt(client, AuthStatus.Ok, null, null)), new StringWriter());
    }

    [Test, NotInParallel]
    [Arguments("Stop", true)]
    [Arguments("UserPromptSubmit", false)]
    [Arguments("PreToolUse", false)]
    public async Task Hosted_turn_boundaries_relay_to_the_daemon_bridge(string eventName, bool waiting) {
        using var bridge = WireMockServer.Start();
        bridge.Given(Request.Create().WithPath("/tok/claude/input-wait").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(204));
        var hosted = new HostedAgent("agent-1", IsRendered: false, new DaemonBridge.Loopback($"http://127.0.0.1:{bridge.Ports[0]}/tok"));

        var exit = await RunAsync(hosted, eventName);

        await Assert.That(exit).IsEqualTo(0);
        var relayed = bridge.LogEntries.Single(e => e.RequestMessage.Path == "/tok/claude/input-wait");
        var body    = JsonNode.Parse(relayed.RequestMessage.Body!)!;
        await Assert.That(body["waiting"]!.GetValue<bool>()).IsEqualTo(waiting);
        await Assert.That(body["agent_id"]!.GetValue<string>()).IsEqualTo("agent-1");
        await Assert.That(body["session_id"]!.GetValue<string>()).IsEqualTo(Sid);
        await Assert.That(body["cwd"]!.GetValue<string>()).IsEqualTo("/tmp");
    }

    /// A session the user runs themselves has a daemon URL only by accident of environment
    /// inheritance; without the agent id nothing identifies it to a daemon.
    [Test, NotInParallel]
    public async Task An_unhosted_stop_relays_nothing() {
        using var bridge = WireMockServer.Start();
        var unhosted = new HostedAgent(null, IsRendered: false, new DaemonBridge.Loopback($"http://127.0.0.1:{bridge.Ports[0]}/tok"));

        await RunAsync(unhosted, "Stop");

        await Assert.That(bridge.LogEntries.Count).IsEqualTo(0);
    }

    /// A subagent's tool call runs the same hook with the parent's environment, but it is not the
    /// parent's turn: a background subagent working on after the parent asked the user something
    /// must not clear the parent's wait.
    [Test, NotInParallel]
    public async Task A_subagents_tool_call_relays_nothing() {
        using var bridge = WireMockServer.Start();
        bridge.Given(Request.Create().WithPath("/tok/claude/input-wait").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(204));
        var hosted = new HostedAgent("agent-1", IsRendered: false, new DaemonBridge.Loopback($"http://127.0.0.1:{bridge.Ports[0]}/tok"));

        var exit = await RunAsync(hosted, "PreToolUse", extraFields: ",\"agent_id\":\"3f2504e04f8911d39a0c0305e82c3301\"");

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(bridge.LogEntries.Count).IsEqualTo(0);
    }

    /// The relay spends the hook's own budget, never more: once that is gone the hint is dropped
    /// rather than pushing the policy decision past the host's kill.
    [Test, NotInParallel]
    public async Task An_exhausted_hook_budget_skips_the_relay() {
        using var bridge = WireMockServer.Start();
        bridge.Given(Request.Create().WithPath("/tok/claude/input-wait").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(204));
        var hosted = new HostedAgent("agent-1", IsRendered: false, new DaemonBridge.Loopback($"http://127.0.0.1:{bridge.Ports[0]}/tok"));

        var exit = await RunAsync(hosted, "PreToolUse", Aged(TimeSpan.FromSeconds(10)));

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(bridge.LogEntries.Count).IsEqualTo(0);
    }
}
