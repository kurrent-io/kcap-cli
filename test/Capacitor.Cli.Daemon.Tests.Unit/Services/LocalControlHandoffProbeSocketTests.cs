using System.Text.Json;
using System.Net.Sockets;
using Capacitor.Cli.Core.Handoff;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>A HandoffProbe frame sent over the real daemon socket is answered with a HandoffProbeReply.</summary>
[ExcludeOn(OS.Windows)] // Unix-domain socket path
public class LocalControlHandoffProbeSocketTests {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    sealed class NoopHostLifetime : IHostApplicationLifetime {
        public CancellationToken ApplicationStarted  => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped  => CancellationToken.None;
        public void StopApplication() { }
    }

    sealed class NoopPtyProcessFactory : IPtyProcessFactory {
        public IPtyProcess Spawn(
                string command, string[] args, string cwd,
                Dictionary<string, string>? extraEnv = null, ushort cols = 120, ushort rows = 40,
                IReadOnlyCollection<string>? removeEnv = null
            ) => throw new NotSupportedException("LocalControlHelloTests never spawns a PTY");
    }

    sealed class NoopHttpClientFactory : IHttpClientFactory {
        public HttpClient CreateClient(string name) => new();
    }

    sealed class NoopRestartStrategy : IRestartStrategy {
        public RestartOutcome Restart() => RestartOutcome.NoOp;
    }

    sealed record Harness(TempDaemonStore Daemons, LocalControlServer Server, AgentOrchestrator Orchestrator, ServerConnection Connection, DaemonConfig Config, string SockPath);

    async Task<Harness> StartAsync(string daemonName, CancellationToken ct) {
        var daemons     = new TempDaemonStore();
        var stateRoot   = daemons.Store.StateDirectory(daemonName);
        var store       = new LaunchConsentStore(stateRoot, NullLogger.Instance, TimeProvider.System);
        var broker      = new LaunchConsentBroker();
        var decisionLog = new LaunchConsentDecisionLog(stateRoot, NullLogger.Instance);
        var gate        = new LaunchConsentGate(store, decisionLog, broker, TimeProvider.System, NullLogger<LaunchConsentGate>.Instance);

        var config = new DaemonConfig {
            Name         = daemonName,
            ServerUrl    = "http://127.0.0.1:1",
            Store        = daemons.Store,
            WorktreeRoot = daemons.PathTo("worktrees"),
        };
        var consentIpc  = new LaunchConsentIpc(broker, store, config, NullLogger<LaunchConsentIpc>.Instance);

        var tokens           = AuthFixtures.NewTokenStore(Config.Root);
        var connection       = new ServerConnection(config, new FixedCredentialSource(), NullLoggerFactory.Instance, NullLogger<ServerConnection>.Instance, TimeProvider.System);
        var worktreeManager  = new WorktreeManager(config, NullLogger<WorktreeManager>.Instance, NoSnapshotBarrier.Instance, TimeProvider.System);
        var repoMatcher      = new RepoMatcher(config, NullLogger<RepoMatcher>.Instance, TimeProvider.System);
        var permissionBridge = new LocalPermissionBridge(connection, NullLogger<LocalPermissionBridge>.Instance, EphemeralLoopbackPortSource.Instance, TimeProvider.System);

        var orchestrator = new AgentOrchestrator(
            config, Config.Root, TestHarnesses.Under(Home), connection, worktreeManager, repoMatcher,
            new NoopPtyProcessFactory(), new NoopHttpClientFactory(), new FixedCapacitorHttpClient(),
            tokens,
            permissionBridge, new Dictionary<string, IHostedAgentLauncher>(),
            new Dictionary<string, IHostedAgentRuntimeFactory>(), new NoopHostLifetime(),
            NullLogger<AgentOrchestrator>.Instance, gate, TimeProvider.System);

        var permissionIpc = new PermissionIpc(new PermissionPromptBroker(), NullLogger<PermissionIpc>.Instance);
        var notifier = new DaemonStatusNotifier();
        var statusIpc = new DaemonStatusIpc(config, orchestrator, connection, notifier, TimeProvider.System);
        var settingsIpc = new DaemonSettingsIpc(config, orchestrator, notifier, NullLogger<DaemonSettingsIpc>.Instance);
        var restart = RestartCoordinator.ForTest(daemons.Store, daemonName, daemonName, new NoopRestartStrategy(), TimeProvider.System);
        var server = new LocalControlServer(config, orchestrator, restart, consentIpc, permissionIpc, statusIpc, settingsIpc, TestFences.Ipc(config, orchestrator), TestShutdown.Ipc(orchestrator, restart), TestAccountsIpc.Inert(), NullLogger<LocalControlServer>.Instance);
        await server.StartAsync(ct);

        var sockPath = daemons.Store.SocketPath(daemonName);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(sockPath) && DateTime.UtcNow < deadline) await Task.Delay(20, ct);

        return new Harness(daemons, server, orchestrator, connection, config, sockPath);
    }

    static async Task StopAsync(Harness h) {
        await h.Orchestrator.DisposeAsync();
        await h.Server.StopAsync(CancellationToken.None);
        h.Server.Dispose();
        await h.Connection.DisposeAsync();
        h.Daemons.Dispose();
    }

    /// Wraps a test body with the harness lifecycle, mirroring LaunchConsentIpcTests's RunAsync. The harness owns
    /// its own daemons directory, so nothing here is shared between tests; each [Test] still
    /// carries its own Windows guard, which must be visible on the test method itself.
    async Task RunAsync(string daemonName, Func<Harness, CancellationToken, Task> body) {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        Harness? h = null;
        try {
            h = await StartAsync(daemonName, cts.Token);
            await Assert.That(File.Exists(h.SockPath)).IsTrue();
            await body(h, cts.Token);
        } finally {
            if (h is not null) await StopAsync(h);
        }
    }

    static async Task<NetworkStream> ConnectAsync(string sockPath, CancellationToken ct) {
        // Bind publishes the socket file before Listen. A connect in that gap is refused.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (true) {
            var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try {
                await sock.ConnectAsync(new UnixDomainSocketEndPoint(sockPath), ct);
                return new NetworkStream(sock, ownsSocket: true);
            } catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused) {
                sock.Dispose();
                if (DateTime.UtcNow >= deadline) throw;
                await Task.Delay(20, ct);
            }
        }
    }

    static async Task<HandoffProbeReplyDto> ProbeAsync(Harness h, string payload, CancellationToken ct) {
        await using var s = await ConnectAsync(h.SockPath, ct);
        await FrameCodec.WriteAsync(s, LocalFrame.HandoffJson(FrameType.HandoffProbe, payload), ct);
        var reply = await FrameCodec.ReadAsync(s, ct);
        await Assert.That(reply!.Type).IsEqualTo(FrameType.HandoffProbeReply);

        return JsonSerializer.Deserialize(reply.Text, HandoffIpcJsonContext.Default.HandoffProbeReplyDto)!;
    }

    [Test]
    public async Task A_malformed_payload_is_answered_as_an_invalid_target() =>
        await RunAsync("handoff-a", async (h, ct) =>
            await Assert.That((await ProbeAsync(h, "not json", ct)).Refusal).IsEqualTo(HandoffRefusals.InvalidTarget));

    [Test]
    public async Task A_path_that_does_not_exist_is_refused() =>
        await RunAsync("handoff-b", async (h, ct) => {
            var request = new HandoffProbeRequestDto(h.Daemons.PathTo("absent"), "o", "n", "b", null, false, null, null, null);
            var json    = JsonSerializer.Serialize(request, HandoffIpcJsonContext.Default.HandoffProbeRequestDto);

            await Assert.That((await ProbeAsync(h, json, ct)).Refusal).IsEqualTo(HandoffRefusals.RepoNotAllowed);
        });

    /// <summary>The app reads a hand-off's outcome by the successor's session id through the client it uses for
    /// every other one-shot operation.</summary>
    [Test]
    public async Task A_stored_outcome_is_read_over_the_socket() =>
        await RunAsync("handoff-c", async (h, ct) => {
            const string successor = "0f0f0f0f-2222-4222-8222-000000000002";
            new HandoffOutcomeStore(h.Daemons.Store.StateDirectory("handoff-c"), TimeProvider.System)
                .Put(successor, new HandoffOutcomeDto("src-1", "Wire it", "done", null, []));
            var ops = new LocalControlOps(h.Daemons.Store, "handoff-c", TimeProvider.System);

            await Assert.That((await ops.ReadHandoffOutcomeAsync(successor, ct))!.SourceTitle).IsEqualTo("Wire it");
            await Assert.That(await ops.ReadHandoffOutcomeAsync("0f0f0f0f-2222-4222-8222-000000000003", ct)).IsNull();
        });
}
