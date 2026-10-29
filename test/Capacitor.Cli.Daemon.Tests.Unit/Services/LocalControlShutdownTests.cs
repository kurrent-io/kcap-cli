using System.Net.Sockets;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>Drives the Shutdown frame through a real <see cref="LocalControlServer"/> socket.</summary>
[ExcludeOn(OS.Windows)] // Unix-domain socket path
public class LocalControlShutdownTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }

    sealed class CountingLifetime : IHostApplicationLifetime {
        public int StopCalls;
        public CancellationToken ApplicationStarted  => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped  => CancellationToken.None;
        public void StopApplication() => Interlocked.Increment(ref StopCalls);

        /// <summary>The ack is written before the stop is requested, so a client that has read it can run ahead of the call.</summary>
        public async Task WaitForStopAsync() {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Volatile.Read(ref StopCalls) == 0 && DateTime.UtcNow < deadline) await Task.Delay(10);
        }
    }

    sealed class SpyStrategy : IRestartStrategy {
        public int            Calls;
        public RestartOutcome Restart() { Calls++; return RestartOutcome.Initiated; }
    }

    sealed class NoopPtyProcessFactory : IPtyProcessFactory {
        public IPtyProcess Spawn(
                string command, string[] args, string cwd,
                Dictionary<string, string>? extraEnv = null, ushort cols = 120, ushort rows = 40,
                IReadOnlyCollection<string>? removeEnv = null
            ) => throw new NotSupportedException("never spawns a PTY");
    }

    sealed class NoopHttpClientFactory : IHttpClientFactory {
        public HttpClient CreateClient(string name) => new();
    }

    async Task RunAsync(string name, Func<bool>? hardBusy, Func<AgentOrchestrator, SpyStrategy, CountingLifetime, Func<LocalFrame, Task<LocalFrame?>>, Task> body) {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var daemons = new TempDaemonStore();

        var stateRoot = daemons.Store.StateDirectory(name);
        var store     = new LaunchConsentStore(stateRoot, NullLogger.Instance, TimeProvider.System);
        var broker    = new LaunchConsentBroker();
        var gate      = new LaunchConsentGate(store, new LaunchConsentDecisionLog(stateRoot, NullLogger.Instance), broker, TimeProvider.System, NullLogger<LaunchConsentGate>.Instance);
        var config    = new DaemonConfig {
            Name         = name,
            ServerUrl    = "http://127.0.0.1:1",
            Store        = daemons.Store,
            WorktreeRoot = daemons.PathTo("worktrees"),
        };
        var consentIpc = new LaunchConsentIpc(broker, store, config, NullLogger<LaunchConsentIpc>.Instance);

        var tokens     = AuthFixtures.NewTokenStore(Config.Root);
        var connection = new ServerConnection(config, new FixedCredentialSource(), NullLoggerFactory.Instance, NullLogger<ServerConnection>.Instance, TimeProvider.System);
        var lifetime   = new CountingLifetime();
        var orchestrator = new AgentOrchestrator(
            config, Config.Root, TestHarnesses.Under(Home), connection,
            new WorktreeManager(config, NullLogger<WorktreeManager>.Instance, NoSnapshotBarrier.Instance, TimeProvider.System),
            new RepoMatcher(config, NullLogger<RepoMatcher>.Instance, TimeProvider.System),
            new NoopPtyProcessFactory(), new NoopHttpClientFactory(), new FixedCapacitorHttpClient(), tokens,
            new LocalPermissionBridge(connection, NullLogger<LocalPermissionBridge>.Instance, EphemeralLoopbackPortSource.Instance, TimeProvider.System),
            new Dictionary<string, IHostedAgentLauncher>(), new Dictionary<string, IHostedAgentRuntimeFactory>(), lifetime,
            NullLogger<AgentOrchestrator>.Instance, gate, TimeProvider.System);

        var spy     = new SpyStrategy();
        var restart = RestartCoordinator.ForTest(daemons.Store, name, name, spy, TimeProvider.System);
        if (hardBusy is not null) restart.IsHardBusy = hardBusy;

        var notifier = new DaemonStatusNotifier();
        var server = new LocalControlServer(
            config, orchestrator, restart, consentIpc,
            new PermissionIpc(new PermissionPromptBroker(), NullLogger<PermissionIpc>.Instance),
            new DaemonStatusIpc(config, orchestrator, connection, notifier, TimeProvider.System),
            new DaemonSettingsIpc(config, orchestrator, notifier, NullLogger<DaemonSettingsIpc>.Instance),
            TestFences.Ipc(config, orchestrator), TestShutdown.Ipc(orchestrator, restart, lifetime), TestAccountsIpc.Inert(), NullLogger<LocalControlServer>.Instance);
        await server.StartAsync(cts.Token);

        try {
            var sockPath = daemons.Store.SocketPath(name);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!File.Exists(sockPath) && DateTime.UtcNow < deadline) await Task.Delay(20, cts.Token);

            async Task<LocalFrame?> Send(LocalFrame frame) {
                // Bind creates the socket file before Listen makes it ready to accept connections.
                var connectDeadline = DateTime.UtcNow.AddSeconds(2);
                while (true) {
                    using var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    try {
                        await sock.ConnectAsync(new UnixDomainSocketEndPoint(sockPath), cts.Token);
                    } catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused) {
                        if (DateTime.UtcNow >= connectDeadline) throw;
                        await Task.Delay(20, cts.Token);
                        continue;
                    }
                    await using var stream = new NetworkStream(sock, ownsSocket: false);
                    await FrameCodec.WriteAsync(stream, frame, cts.Token);
                    return await FrameCodec.ReadAsync(stream, cts.Token);
                }
            }

            await body(orchestrator, spy, lifetime, Send);
        } finally {
            await orchestrator.DisposeAsync();
            await server.StopAsync(CancellationToken.None);
            server.Dispose();
            await connection.DisposeAsync();
        }
    }

    [Test]
    public async Task Shutdown_keep_relaunch_sets_intent_and_requests_a_forced_restart_and_acks_stopping() =>
        await RunAsync("sd-keep", null, async (orch, spy, lifetime, send) => {
            orch.ShutdownIntent = ShutdownIntent.EndAgents;

            var reply = await send(LocalFrame.Shutdown(new ShutdownRequestDto("keep", "relaunch")));

            await Assert.That(reply!.Type).IsEqualTo(FrameType.ShutdownAck);
            await Assert.That(reply.Text).IsEqualTo("stopping");
            await Assert.That(orch.ShutdownIntent).IsEqualTo(ShutdownIntent.KeepAgents);
            await Assert.That(spy.Calls).IsEqualTo(1);
            await Assert.That(lifetime.StopCalls).IsEqualTo(0);
        });

    [Test]
    public async Task Shutdown_end_final_sets_intent_stops_the_application_and_acks_stopping() =>
        await RunAsync("sd-end", null, async (orch, spy, lifetime, send) => {
            var reply = await send(LocalFrame.Shutdown(new ShutdownRequestDto("end", "final")));

            await Assert.That(reply!.Type).IsEqualTo(FrameType.ShutdownAck);
            await Assert.That(reply.Text).IsEqualTo("stopping");
            await Assert.That(orch.ShutdownIntent).IsEqualTo(ShutdownIntent.EndAgents);
            await lifetime.WaitForStopAsync();
            await Assert.That(lifetime.StopCalls).IsEqualTo(1);
            await Assert.That(spy.Calls).IsEqualTo(0);
        });

    [Test]
    public async Task Shutdown_relaunch_while_an_eval_is_in_flight_acks_queued() =>
        await RunAsync("sd-queued", () => true, async (orch, spy, lifetime, send) => {
            var reply = await send(LocalFrame.Shutdown(new ShutdownRequestDto("keep", "relaunch")));

            await Assert.That(reply!.Type).IsEqualTo(FrameType.ShutdownAck);
            await Assert.That(reply.Text).IsEqualTo("queued");
            await Assert.That(spy.Calls).IsEqualTo(0);
        });

    /// <summary>An unattended stop must not end an eval or lose a launch in admission; the intent stays
    /// as it was because nothing stops.</summary>
    [Test]
    public async Task Shutdown_when_idle_while_busy_answers_busy_and_does_not_stop() =>
        await RunAsync("sd-busy", () => true, async (orch, spy, lifetime, send) => {
            orch.ShutdownIntent = ShutdownIntent.EndAgents;

            var reply = await send(LocalFrame.Shutdown(new ShutdownRequestDto("keep", "final", WhenIdle: true)));

            await Assert.That(reply!.Type).IsEqualTo(FrameType.Error);
            await Assert.That(reply.Text).StartsWith("busy:");
            await Assert.That(orch.ShutdownIntent).IsEqualTo(ShutdownIntent.EndAgents);
            await Assert.That(lifetime.StopCalls).IsEqualTo(0);
            await Assert.That(spy.Calls).IsEqualTo(0);
        });

    [Test]
    public async Task Shutdown_final_without_when_idle_stops_even_while_busy() =>
        await RunAsync("sd-busy-now", () => true, async (orch, spy, lifetime, send) => {
            var reply = await send(LocalFrame.Shutdown(new ShutdownRequestDto("keep", "final")));

            await Assert.That(reply!.Type).IsEqualTo(FrameType.ShutdownAck);
            await lifetime.WaitForStopAsync();
            await Assert.That(lifetime.StopCalls).IsEqualTo(1);
        });

    [Test]
    public async Task Malformed_shutdown_answers_an_error() =>
        await RunAsync("sd-bad", null, async (orch, spy, lifetime, send) => {
            var reply = await send(new LocalFrame(FrameType.Shutdown) { Text = """{"agents":"maybe","exit":"final"}""" });

            await Assert.That(reply!.Type).IsEqualTo(FrameType.Error);
            await Assert.That(spy.Calls).IsEqualTo(0);
            await Assert.That(lifetime.StopCalls).IsEqualTo(0);
        });
}
