using System.Net.Sockets;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Logging.Abstractions;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// DaemonSettingsPut over a real Unix socket through the routing switch: a valid put changes the
/// live cap the next launch and the next status snapshot read, and republishes the registration;
/// an invalid or malformed put changes nothing and names why.
/// </summary>
[ExcludeOn(OS.Windows)] // Unix-domain socket path
public class DaemonSettingsIpcTests {
    sealed class NoopRestartStrategy : IRestartStrategy {
        public RestartOutcome Restart() => RestartOutcome.NoOp;
    }

    sealed record Harness(LocalControlServer Server, AgentOrchestrator Orchestrator, DaemonConfig Config, string SockPath);

    /// The server double is the test's own: the counting one for republish assertions, the
    /// sequenced one for a launch that must settle as a rejection.
    static async Task<Harness> StartAsync(ServerConnection server, CancellationToken ct) {
        DaemonConfig? captured = null;
        var orchestrator = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher> { ["claude"] = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude") },
            configure: c => captured = c);
        var config = captured!;

        var stateRoot   = config.Store.StateDirectory(config.Name);
        var consentIpc  = new LaunchConsentIpc(new LaunchConsentBroker(), new LaunchConsentStore(stateRoot, NullLogger.Instance), config, NullLogger<LaunchConsentIpc>.Instance);
        var permissionIpc = new PermissionIpc(new PermissionPromptBroker(), NullLogger<PermissionIpc>.Instance);
        var notifier    = new DaemonStatusNotifier();
        var statusIpc   = new DaemonStatusIpc(config, orchestrator, server, notifier);
        var settingsIpc = new DaemonSettingsIpc(config, orchestrator, notifier, NullLogger<DaemonSettingsIpc>.Instance);
        var restart     = RestartCoordinator.ForTest(config.Store, config.Name, "test", new NoopRestartStrategy());
        var control     = new LocalControlServer(config, orchestrator, restart, consentIpc, permissionIpc, statusIpc, settingsIpc, NullLogger<LocalControlServer>.Instance);
        await control.StartAsync(ct);

        var sockPath = config.Store.SocketPath(config.Name);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(sockPath) && DateTime.UtcNow < deadline) await Task.Delay(20, ct);

        return new Harness(control, orchestrator, config, sockPath);
    }

    static async Task StopAsync(Harness h) {
        await h.Orchestrator.DisposeAsync();
        await h.Server.StopAsync(CancellationToken.None);
        h.Server.Dispose();
    }

    static async Task RunAsync(ServerConnection server, Func<Harness, CancellationToken, Task> body) {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Harness? h = null;
        try {
            h = await StartAsync(server, cts.Token);
            await Assert.That(File.Exists(h.SockPath)).IsTrue();
            await body(h, cts.Token);
        } finally {
            if (h is not null) await StopAsync(h);
        }
    }

    static async Task<NetworkStream> ConnectAsync(string sockPath, CancellationToken ct) {
        var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await sock.ConnectAsync(new UnixDomainSocketEndPoint(sockPath), ct);
        return new NetworkStream(sock, ownsSocket: true);
    }

    static async Task<DaemonSettingsAckDto> PutAsync(Harness h, string payload, CancellationToken ct) {
        await using var s = await ConnectAsync(h.SockPath, ct);
        await FrameCodec.WriteAsync(s, LocalFrame.SettingsJson(FrameType.DaemonSettingsPut, payload), ct);
        var reply = await FrameCodec.ReadAsync(s, ct);
        await Assert.That(reply!.Type).IsEqualTo(FrameType.DaemonSettingsAck);
        return JsonSerializer.Deserialize(reply.Text, SettingsIpcJsonContext.Default.DaemonSettingsAckDto)!;
    }

    static async Task<DaemonStatusDto> FirstSnapshotAsync(Harness h, CancellationToken ct) {
        await using var s = await ConnectAsync(h.SockPath, ct);
        await FrameCodec.WriteAsync(s, new LocalFrame(FrameType.StatusSubscribe), ct);
        var frame = await FrameCodec.ReadAsync(s, ct);
        await Assert.That(frame!.Type).IsEqualTo(FrameType.DaemonStatus);
        return JsonSerializer.Deserialize(frame.Text, StatusIpcJsonContext.Default.DaemonStatusDto)!;
    }

    [Test]
    public async Task A_valid_put_applies_live_shows_in_the_snapshot_and_republishes() {
        var server = new CaptureServerConnection();
        await RunAsync(server, async (h, ct) => {
            var ack = await PutAsync(h, """{"max_agents":2}""", ct);

            await Assert.That(ack).IsEqualTo(new DaemonSettingsAckDto(true, null, 2));
            await Assert.That(h.Config.MaxConcurrentAgents).IsEqualTo(2);
            await Assert.That((await FirstSnapshotAsync(h, ct)).Daemon.MaxAgents).IsEqualTo(2);

            await h.Orchestrator.CapabilityRefreshForTest;
            await Assert.That(server.RegisterDaemonCalls).IsEqualTo(1);
        });
    }

    [Test]
    public async Task A_live_subscriber_is_pushed_the_new_cap_after_a_put() {
        await RunAsync(new CaptureServerConnection(), async (h, ct) => {
            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, new LocalFrame(FrameType.StatusSubscribe), ct);
            var first = await FrameCodec.ReadAsync(s, ct);
            await Assert.That(JsonSerializer.Deserialize(first!.Text, StatusIpcJsonContext.Default.DaemonStatusDto)!.Daemon.MaxAgents).IsEqualTo(5);

            await PutAsync(h, """{"max_agents":2}""", ct);

            var second = await FrameCodec.ReadAsync(s, ct);
            await Assert.That(second!.Type).IsEqualTo(FrameType.DaemonStatus);
            await Assert.That(JsonSerializer.Deserialize(second.Text, StatusIpcJsonContext.Default.DaemonStatusDto)!.Daemon.MaxAgents).IsEqualTo(2);
        });
    }

    [Test]
    public async Task The_next_launch_over_the_new_cap_is_refused() {
        var server = new SeqCaptureServerConnection();
        await RunAsync(server, async (h, ct) => {
            h.Orchestrator.SeedAgentForTest("s1");
            await PutAsync(h, """{"max_agents":1}""", ct);

            await h.Orchestrator.HandleLaunchAgentForTest(new LaunchAgentCommand(
                AgentId: "cap", Prompt: "hi", Model: "opus", Effort: null,
                RepoPath: "/tmp/does-not-matter", Tools: null, AttachmentIds: null, Vendor: "claude",
                Epoch: h.Orchestrator.DaemonEpochForTest, Seq: 1, CommandId: "cmd-1"));
            await WaitHarness.SpinUntilAsync(() => server.Rejects.Count > 0, TimeSpan.FromSeconds(10));

            await Assert.That(server.Rejects.Single().Reason).IsEqualTo(CommandRejectedReason.DaemonCapacity);
            await Assert.That(h.Orchestrator.ReadLiveness("s1")).IsEqualTo(AgentLiveness.Live);
        });
    }

    [Test]
    [Arguments("not json", "malformed")]
    [Arguments("[]", "malformed")]
    [Arguments("{}", "malformed")]
    [Arguments("""{"max_agents":null}""", "malformed")]
    [Arguments("""{"max_agents":-3}""", "invalid_max_agents")]
    public async Task An_invalid_put_changes_nothing_and_names_why(string payload, string reason) {
        var server = new CaptureServerConnection();
        await RunAsync(server, async (h, ct) => {
            var ack = await PutAsync(h, payload, ct);

            await Assert.That(ack).IsEqualTo(new DaemonSettingsAckDto(false, reason, 5));
            await Assert.That(h.Config.MaxConcurrentAgents).IsEqualTo(5);
            await h.Orchestrator.CapabilityRefreshForTest;
            await Assert.That(server.RegisterDaemonCalls).IsEqualTo(0);
        });
    }

    [Test]
    public async Task Zero_max_agents_is_accepted_as_unlimited() {
        await RunAsync(new CaptureServerConnection(), async (h, ct) => {
            var ack = await PutAsync(h, """{"max_agents":0}""", ct);

            await Assert.That(ack).IsEqualTo(new DaemonSettingsAckDto(true, null, 0));
            await Assert.That(h.Config.MaxConcurrentAgents).IsEqualTo(0);
        });
    }

    [Test]
    public async Task Unlimited_capacity_admits_a_launch_past_the_capacity_gate() {
        var server = new SeqCaptureServerConnection();
        await RunAsync(server, async (h, ct) => {
            h.Orchestrator.SeedAgentForTest("s1");
            h.Orchestrator.SeedAgentForTest("s2");
            await PutAsync(h, """{"max_agents":0}""", ct);

            await h.Orchestrator.HandleLaunchAgentForTest(new LaunchAgentCommand(
                AgentId: "past", Prompt: "hi", Model: "opus", Effort: null,
                RepoPath: "/tmp/does-not-matter", Tools: null, AttachmentIds: null, Vendor: "claude",
                Epoch: h.Orchestrator.DaemonEpochForTest, Seq: 1, CommandId: "cmd-1"));
            await WaitHarness.SpinUntilAsync(() => server.Rejects.Count > 0, TimeSpan.FromSeconds(10));

            // Reached repo validation (Semantic) rather than being rejected for capacity — the
            // gate is skipped when unlimited, even with two agents already seeded.
            await Assert.That(server.Rejects.Single().Reason).IsEqualTo(CommandRejectedReason.Semantic);
        });
    }

    [Test]
    public async Task The_core_client_round_trips_a_put() {
        await RunAsync(new CaptureServerConnection(), async (h, ct) => {
            var ops = new LocalControlOps(h.Config.Store, h.Config.Name);

            var ack = await ops.PutDaemonSettingsAsync(new DaemonSettingsPutDto(4), ct);

            await Assert.That(ack).IsEqualTo(new DaemonSettingsAckDto(true, null, 4));
            await Assert.That(h.Config.MaxConcurrentAgents).IsEqualTo(4);
        });
    }

    [Test]
    public async Task Hello_advertises_settings_1() {
        await RunAsync(new CaptureServerConnection(), async (h, ct) => {
            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, new LocalFrame(FrameType.Hello), ct);
            var reply = await FrameCodec.ReadAsync(s, ct);
            var dto = JsonSerializer.Deserialize(reply!.Text, HelloIpcJsonContext.Default.HelloReplyDto)!;

            await Assert.That(dto.Capabilities).Contains(SettingsWire.Capability);
        });
    }
}
