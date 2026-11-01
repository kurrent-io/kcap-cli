using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Commands.Harness;
using Microsoft.Extensions.Time.Testing;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Capacitor.Cli.Core;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Tests.Unit.Commands.Harness;

/// The input-wait relay a daemon-hosted Codex session sends its daemon. On a clock that never moves, so
/// the relay's cap cannot run out under a loaded host.
public class CodexHookInputWaitRelayTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    /// The payload carries no transcript, so neither the watcher nor the server post runs: the
    /// relay is the only thing this hook can reach the network with.
    [Test]
    [Arguments("Stop", true)]
    [Arguments("UserPromptSubmit", false)]
    public async Task Hosted_turn_boundaries_relay_to_the_daemon_bridge(string eventName, bool waiting) {
        using var bridge = WireMockServer.Start();
        bridge.Given(Request.Create().WithPath("/tok/codex/input-wait").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(204));
        var hosted = new HostedAgent("agent-1", IsRendered: false, new DaemonBridge.Loopback($"http://127.0.0.1:{bridge.Ports[0]}/tok"));

        var exit = await new CodexHookCommand(Config.Root, Resolutions.At("http://server.example", Config.Root), new HookClock(new FakeTimeProvider()), Home, TestHarnesses.Under(Home), hosted, new FixedCapacitorHttpClient(), TestWatchers.For(Config.Root, Resolutions.At("http://server.example", Config.Root), new FixedCapacitorHttpClient()), router: new GitProviderRouter(), workdir: new WorkingDirectory(AppContext.BaseDirectory), accounts: TestAccounts.None)
            .Handle(new StringReader($$"""{"hook_event_name":"{{eventName}}","session_id":"019e0322-05fc-7570-be65-75719c3ea861","cwd":"/tmp"}"""),
                new CodexHookEnvironment(TextWriter.Null, TextWriter.Null, Skip: false, HostedAppServer: false));

        await Assert.That(exit).IsEqualTo(0);
        var relayed = bridge.LogEntries.Single(e => e.RequestMessage.Path == "/tok/codex/input-wait");
        var body    = JsonDocument.Parse(relayed.RequestMessage.Body!).RootElement;
        await Assert.That(body.GetProperty("waiting").GetBoolean()).IsEqualTo(waiting);
        await Assert.That(body.GetProperty("agent_id").GetString()).IsEqualTo("agent-1");
        await Assert.That(body.GetProperty("session_id").GetString()).IsEqualTo("019e032205fc7570be6575719c3ea861");
    }

    /// A bridge that takes the connection and never answers is given the relay's cap on the hook's own
    /// clock, and no more: the hook still answers Codex once that time has passed.
    [Test]
    public async Task A_bridge_that_never_answers_holds_the_hook_only_for_the_relays_cap() {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port     = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = System.Threading.Channels.Channel.CreateUnbounded<System.Net.Sockets.Socket>();
        _ = Task.Run(async () => {
            try {
                while (true) await accepted.Writer.WriteAsync(await listener.AcceptSocketAsync());
            } catch (Exception ex) when (ex is ObjectDisposedException or System.Net.Sockets.SocketException) { }
        });

        var time   = new FakeTimeProvider();
        var stdout = new StringWriter();
        var hosted = new HostedAgent("agent-1", IsRendered: false, new DaemonBridge.Loopback($"http://127.0.0.1:{port}/tok"));
        var run = new CodexHookCommand(Config.Root, Resolutions.At("http://server.example", Config.Root), new HookClock(time), Home, TestHarnesses.Under(Home), hosted, new FixedCapacitorHttpClient(), TestWatchers.For(Config.Root, Resolutions.At("http://server.example", Config.Root), new FixedCapacitorHttpClient()), router: new GitProviderRouter(), workdir: new WorkingDirectory(AppContext.BaseDirectory), accounts: TestAccounts.None)
            .Handle(new StringReader("""{"hook_event_name":"Stop","session_id":"019e0322-05fc-7570-be65-75719c3ea861","cwd":"/tmp"}"""),
                new CodexHookEnvironment(stdout, TextWriter.Null, Skip: false, HostedAppServer: false));

        // Stop sends two notices, each to a bridge that holds it open; each is released by the cap alone.
        var held = new List<System.Net.Sockets.Socket>();
        try {
            for (var notices = 0; notices < 2; notices++) {
                held.Add(await accepted.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
                time.Advance(DaemonBridgeRelay.Cap);
            }

            await Assert.That(await run.WaitAsync(TimeSpan.FromSeconds(10))).IsEqualTo(0);
            await Assert.That(JsonDocument.Parse(stdout.ToString()).RootElement.GetProperty("continue").GetBoolean()).IsTrue();
        } finally {
            foreach (var socket in held) socket.Dispose();
        }
    }
}
