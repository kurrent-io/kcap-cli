using System.Net;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.PrDetection;
using Microsoft.Extensions.Time.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands.Harness;

/// The tool-settled relay a daemon-hosted Claude session sends its daemon so a prompt answered in
/// the terminal is retired: which events reach it, with what scope, and what silences it. On a clock
/// that never moves unless a test ages it, so the relay's cap cannot run out under a loaded host.
public class ClaudeHookToolSettledRelayTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Sid = "9dc2775376454e4691ecc2d69973c152";
    const string Sub = "3f2504e04f8911d39a0c0305e82c3301";

    /// Answers every server post with 200 and counts them.
    sealed class CountingHandler : HttpMessageHandler {
        public int Posts;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) {
            Interlocked.Increment(ref Posts);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

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
        bridge.Given(Request.Create().WithPath("/tok/claude/tool-settled").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(204));
        bridge.Given(Request.Create().WithPath("/tok/claude/input-wait").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(204));
        return bridge;
    }

    static JsonNode? Relayed(WireMockServer bridge) {
        var entry = bridge.LogEntries.SingleOrDefault(e => e.RequestMessage.Path == "/tok/claude/tool-settled");
        return entry is null ? null : JsonNode.Parse(entry.RequestMessage.Body!);
    }

    async Task<(int Exit, int ServerPosts)> RunAsync(HostedAgent hosted, string eventName, HookClock? clock = null, string extraFields = "",
            string toolInput = """{"command":"ls"}""", string toolName = "Bash") {
        var handler = new CountingHandler();
        using var client = new HttpClient(handler);
        var payload = $$$"""{"hook_event_name":"{{{eventName}}}","session_id":"{{{Sid}}}","cwd":"/tmp","tool_name":"{{{toolName}}}","tool_input":{{{toolInput}}}{{{extraFields}}}}""";
        var exit = await new ClaudeHookCommand(Config.Root, Resolutions.At("http://server.example", Config.Root), clock ?? new HookClock(new FakeTimeProvider()), Home, TestHarnesses.Under(Home), hosted, new FixedCapacitorHttpClient(), TestWatchers.For(Config.Root, Resolutions.At("http://server.example", Config.Root), new FixedCapacitorHttpClient()), SystemProcessStarter.Instance, router: new GitProviderRouter(), workdir: new WorkingDirectory(AppContext.BaseDirectory), accounts: TestAccounts.None)
            .HandleWithDeps(new HookSpool(Config.Root, time: TimeProvider.System, server: null), new StringReader(payload), () => Task.FromResult(new AuthAttempt(client, AuthStatus.Ok, null, null)), new StringWriter());
        return (exit, handler.Posts);
    }

    /// The server has no route for a tool's completion and the transcript watcher already carries
    /// its result, so the hook's only work is the notice. It carries the call's name and input too,
    /// which are all Claude's id-less permission prompt can be matched by.
    [Test]
    [Arguments("PostToolUse")]
    [Arguments("PostToolUseFailure")]
    public async Task A_finished_tool_relays_its_id_and_never_reaches_the_server(string eventName) {
        using var bridge = Bridge();

        var (exit, serverPosts) = await RunAsync(HostedOn(bridge), eventName, extraFields: ",\"tool_use_id\":\"toolu_01X\"");

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(serverPosts).IsEqualTo(0);
        var body = Relayed(bridge)!;
        await Assert.That(body["tool_use_id"]!.GetValue<string>()).IsEqualTo("toolu_01X");
        await Assert.That(body["subagent_id"]).IsNull();
        await Assert.That(body["agent_id"]!.GetValue<string>()).IsEqualTo("agent-1");
        await Assert.That(body["session_id"]!.GetValue<string>()).IsEqualTo(Sid);
        await Assert.That(body["cwd"]!.GetValue<string>()).IsEqualTo("/tmp");
        await Assert.That(body["tool_name"]!.GetValue<string>()).IsEqualTo("Bash");
        await Assert.That(body["tool_input"]!.ToJsonString()).IsEqualTo("""{"command":"ls"}""");
        await Assert.That(body["sent_at"]!.GetValue<long>()).IsGreaterThan(0);
    }

    /// A subagent's tool call runs the same hook with the parent's environment. Its id is unique
    /// across the session, but a prompt matched by name and input needs the subagent as well.
    [Test]
    public async Task A_subagents_finished_tool_relays_its_id_and_subagent() {
        using var bridge = Bridge();

        var (exit, _) = await RunAsync(HostedOn(bridge), "PostToolUse", extraFields: $",\"tool_use_id\":\"toolu_02Y\",\"agent_id\":\"{Sub}\"");

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Relayed(bridge)!["tool_use_id"]!.GetValue<string>()).IsEqualTo("toolu_02Y");
        await Assert.That(Relayed(bridge)!["subagent_id"]!.GetValue<string>()).IsEqualTo(Sub);
    }

    /// A name or input past the prompt's caps was never held, and sending it would push the notice
    /// past the bridge's body cap, so both are left out and the id still goes.
    [Test]
    [Arguments(513, 10)]
    [Arguments(4, 64 * 1024)]
    public async Task An_oversized_match_key_is_left_out_and_the_id_still_relayed(int nameLength, int commandLength) {
        using var bridge = Bridge();

        var (exit, _) = await RunAsync(HostedOn(bridge), "PostToolUse", extraFields: ",\"tool_use_id\":\"toolu_03Z\"",
            toolInput: $"{{\"command\":\"{new string('x', commandLength)}\"}}", toolName: new string('B', nameLength));

        await Assert.That(exit).IsEqualTo(0);
        var body = Relayed(bridge)!;
        await Assert.That(body["tool_use_id"]!.GetValue<string>()).IsEqualTo("toolu_03Z");
        await Assert.That(body["tool_name"]).IsNull();
        await Assert.That(body["tool_input"]).IsNull();
    }

    /// A tool that ran without its id cannot be named; the turn's end still retires its prompt.
    [Test]
    public async Task A_finished_tool_without_an_id_relays_nothing() {
        using var bridge = Bridge();

        var (exit, serverPosts) = await RunAsync(HostedOn(bridge), "PostToolUse");

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(serverPosts).IsEqualTo(0);
        await Assert.That(Relayed(bridge)).IsNull();
    }

    /// The main agent's turn ending means every prompt of that turn is moot, a terminal deny
    /// included, which no tool completion ever reports.
    [Test]
    public async Task The_main_agents_stop_relays_the_whole_turn() {
        using var bridge = Bridge();

        var (exit, _) = await RunAsync(HostedOn(bridge), "Stop");

        await Assert.That(exit).IsEqualTo(0);
        var body = Relayed(bridge)!;
        await Assert.That(body["tool_use_id"]).IsNull();
        await Assert.That(body["subagent_id"]).IsNull();
        await Assert.That(body["agent_id"]!.GetValue<string>()).IsEqualTo("agent-1");
        await Assert.That(body["sent_at"]!.GetValue<long>()).IsGreaterThan(0).Because("a late turn end must not retire the next turn's prompt");
    }

    [Test]
    public async Task A_subagents_stop_relays_that_subagents_turn() {
        using var bridge = Bridge();

        var (exit, _) = await RunAsync(HostedOn(bridge), "SubagentStop", extraFields: $",\"agent_id\":\"{Sub}\"");

        await Assert.That(exit).IsEqualTo(0);
        var body = Relayed(bridge)!;
        await Assert.That(body["tool_use_id"]).IsNull();
        await Assert.That(body["subagent_id"]!.GetValue<string>()).IsEqualTo(Sub);
        await Assert.That(body["sent_at"]!.GetValue<long>()).IsGreaterThan(0);
        await Assert.That(bridge.LogEntries.Any(e => e.RequestMessage.Path == "/tok/claude/input-wait")).IsFalse();
    }

    /// The permission hook strips the dashes from its agent_id before the bridge sees it, and the
    /// daemon matches the two exactly, so the stop's notice must arrive in the same form.
    [Test]
    public async Task A_subagents_stop_relays_its_id_dashless_as_the_permission_hook_sent_it() {
        using var bridge = Bridge();

        var (exit, _) = await RunAsync(HostedOn(bridge), "SubagentStop", extraFields: ",\"agent_id\":\"3f2504e0-4f89-11d3-9a0c-0305e82c3301\"");

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Relayed(bridge)!["subagent_id"]!.GetValue<string>()).IsEqualTo(Sub);
    }

    /// A session the user runs themselves has a daemon URL only by accident of environment
    /// inheritance; without the agent id nothing identifies it to a daemon, and the hook has no
    /// other work for the event.
    [Test]
    public async Task An_unhosted_finished_tool_relays_nothing_and_posts_nothing() {
        using var bridge = Bridge();
        var unhosted = new HostedAgent(null, IsRendered: false, new DaemonBridge.Loopback($"http://127.0.0.1:{bridge.Ports[0]}/tok"));

        var (exit, serverPosts) = await RunAsync(unhosted, "PostToolUse", extraFields: ",\"tool_use_id\":\"toolu_01X\"");

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(serverPosts).IsEqualTo(0);
        await Assert.That(bridge.LogEntries.Count).IsEqualTo(0);
    }

    /// The relay spends the hook's own budget, never more: once that is gone the notice is dropped
    /// rather than pushing the hook past the host's kill.
    [Test]
    public async Task An_exhausted_hook_budget_skips_the_relay() {
        using var bridge = Bridge();

        var (exit, _) = await RunAsync(HostedOn(bridge), "PostToolUse", Aged(TimeSpan.FromSeconds(10)), ",\"tool_use_id\":\"toolu_01X\"");

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(bridge.LogEntries.Count).IsEqualTo(0);
    }
}
