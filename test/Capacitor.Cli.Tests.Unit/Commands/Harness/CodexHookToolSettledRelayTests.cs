using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
using Capacitor.Cli.PrDetection;
using Microsoft.Extensions.Time.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands.Harness;

/// The tool-settled relay a daemon-hosted Codex session sends its daemon so a prompt answered in
/// Codex's terminal is retired. On a clock that never moves unless a test ages it, so the relay's cap
/// cannot run out under a loaded host.
public class CodexHookToolSettledRelayTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Sid = "019e032205fc7570be6575719c3ea861";
    const string Sub = "01a1535008417c7095e830667b7a04b9";

    static HookClock Aged(TimeSpan elapsed) {
        var time  = new FakeTimeProvider();
        var clock = new HookClock(time);
        time.Advance(elapsed);
        return clock;
    }

    static HostedAgent HostedOn(WireMockServer bridge) =>
        new("agent-1", IsRendered: false, new DaemonBridge.Loopback($"http://127.0.0.1:{bridge.Ports[0]}/tok"));

    static WireMockServer Bridge() {
        var bridge = WireMockServer.Start();
        bridge.Given(Request.Create().WithPath("/tok/codex/tool-settled").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(204));
        bridge.Given(Request.Create().WithPath("/tok/codex/input-wait").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(204));
        return bridge;
    }

    static JsonNode? Relayed(WireMockServer bridge) {
        var entry = bridge.LogEntries.SingleOrDefault(e => e.RequestMessage.Path == "/tok/codex/tool-settled");
        return entry is null ? null : JsonNode.Parse(entry.RequestMessage.Body!);
    }

    async Task<int> RunAsync(HostedAgent hosted, string eventName, HookClock? clock = null, string extraFields = "") {
        var payload = $$$"""{"hook_event_name":"{{{eventName}}}","session_id":"019e0322-05fc-7570-be65-75719c3ea861","cwd":"/tmp","tool_name":"Bash","tool_input":{"command":"ls"}{{{extraFields}}}}""";
        return await new CodexHookCommand(Config.Root, Resolutions.At("http://server.example", Config.Root), clock ?? new HookClock(new FakeTimeProvider()), Home, TestHarnesses.Under(Home), hosted, new FixedCapacitorHttpClient(), TestWatchers.For(Config.Root, Resolutions.At("http://server.example", Config.Root), new FixedCapacitorHttpClient()), router: new GitProviderRouter(), workdir: new WorkingDirectory(AppContext.BaseDirectory), accounts: TestAccounts.None)
            .Handle(new StringReader(payload), new CodexHookEnvironment(TextWriter.Null, TextWriter.Null, Skip: false, HostedAppServer: false));
    }

    /// Codex's permission prompt carries no tool id, so the notice also carries the call's name and
    /// input, which are what the prompt is matched by, and when the hook sent it.
    [Test]
    public async Task A_finished_tool_relays_its_id_name_and_input() {
        using var bridge = Bridge();

        var exit = await RunAsync(HostedOn(bridge), "PostToolUse", extraFields: ",\"tool_use_id\":\"exec-1\"");

        await Assert.That(exit).IsEqualTo(0);
        var body = Relayed(bridge)!;
        await Assert.That(body["tool_use_id"]!.GetValue<string>()).IsEqualTo("exec-1");
        await Assert.That(body["subagent_id"]).IsNull();
        await Assert.That(body["agent_id"]!.GetValue<string>()).IsEqualTo("agent-1");
        await Assert.That(body["session_id"]!.GetValue<string>()).IsEqualTo(Sid);
        await Assert.That(body["tool_name"]!.GetValue<string>()).IsEqualTo("Bash");
        await Assert.That(body["tool_input"]!.ToJsonString()).IsEqualTo("""{"command":"ls"}""");
        await Assert.That(body["sent_at"]!.GetValue<long>()).IsGreaterThan(0);
    }

    /// A Codex subagent's tool hooks share the parent's session and name the subagent in a dashed
    /// <c>agent_id</c>; the permission hook hands it to the bridge dashless, and the two must match.
    [Test]
    public async Task A_subagents_finished_tool_relays_its_subagent_dashless() {
        using var bridge = Bridge();

        var exit = await RunAsync(HostedOn(bridge), "PostToolUse",
            extraFields: ",\"tool_use_id\":\"exec-2\",\"agent_id\":\"01a15350-0841-7c70-95e8-30667b7a04b9\",\"agent_type\":\"default\"");

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Relayed(bridge)!["subagent_id"]!.GetValue<string>()).IsEqualTo(Sub);
    }

    /// The main agent's turn ending means every prompt of that turn is moot, a terminal deny
    /// included, which no tool completion reports. The input-wait hint still goes.
    [Test]
    public async Task The_main_agents_stop_relays_the_whole_turn() {
        using var bridge = Bridge();

        var exit = await RunAsync(HostedOn(bridge), "Stop");

        await Assert.That(exit).IsEqualTo(0);
        var body = Relayed(bridge)!;
        await Assert.That(body["tool_use_id"]).IsNull();
        await Assert.That(body["subagent_id"]).IsNull();
        await Assert.That(body["agent_id"]!.GetValue<string>()).IsEqualTo("agent-1");
        await Assert.That(body["sent_at"]!.GetValue<long>()).IsGreaterThan(0).Because("a late turn end must not retire the next turn's prompt");
        await Assert.That(bridge.LogEntries.Any(e => e.RequestMessage.Path == "/tok/codex/input-wait")).IsTrue();
    }

    /// A stop naming a subagent is not the main agent's turn, and withdrawing the main agent's
    /// prompts on it would deny one still on screen.
    [Test]
    public async Task A_stop_naming_a_subagent_relays_no_turn() {
        using var bridge = Bridge();

        var exit = await RunAsync(HostedOn(bridge), "Stop", extraFields: $",\"agent_id\":\"{Sub}\"");

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Relayed(bridge)).IsNull();
    }

    [Test]
    public async Task An_unhosted_finished_tool_relays_nothing() {
        using var bridge = Bridge();
        var unhosted = new HostedAgent(null, IsRendered: false, new DaemonBridge.Loopback($"http://127.0.0.1:{bridge.Ports[0]}/tok"));

        var exit = await RunAsync(unhosted, "PostToolUse", extraFields: ",\"tool_use_id\":\"exec-1\"");

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(bridge.LogEntries.Count).IsEqualTo(0);
    }

    /// The relay spends the hook's own budget, never more.
    [Test]
    public async Task An_exhausted_hook_budget_skips_the_relay() {
        using var bridge = Bridge();

        var exit = await RunAsync(HostedOn(bridge), "PostToolUse", Aged(TimeSpan.FromSeconds(10)), ",\"tool_use_id\":\"exec-1\"");

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(bridge.LogEntries.Count).IsEqualTo(0);
    }
}
