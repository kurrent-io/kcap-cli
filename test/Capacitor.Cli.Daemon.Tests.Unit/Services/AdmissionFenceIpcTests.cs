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
/// The rename fence over a real Unix socket through the routing switch: the acquiring connection
/// holds it, commit and abort follow on it, and while it stands no new agent is admitted.
/// </summary>
[ExcludeOn(OS.Windows)] // Unix-domain socket path
public class AdmissionFenceIpcTests {
    sealed class NoopRestartStrategy : IRestartStrategy {
        public RestartOutcome Restart() => RestartOutcome.NoOp;
    }

    sealed record Harness(LocalControlServer Server, AgentOrchestrator Orchestrator, DaemonConfig Config, string SockPath);

    static async Task<Harness> StartAsync(ServerConnection server, CancellationToken ct) {
        DaemonConfig? captured = null;
        var orchestrator = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher> { ["claude"] = new SpyHostedAgentLauncher("claude", cliPath: "spy-claude") },
            configure: c => captured = c);
        var config = captured!;

        var stateRoot     = config.Store.StateDirectory(config.Name);
        var consentIpc    = new LaunchConsentIpc(new LaunchConsentBroker(), new LaunchConsentStore(stateRoot, NullLogger.Instance, TimeProvider.System), config, NullLogger<LaunchConsentIpc>.Instance);
        var permissionIpc = new PermissionIpc(new PermissionPromptBroker(), NullLogger<PermissionIpc>.Instance);
        var notifier      = new DaemonStatusNotifier();
        var statusIpc     = new DaemonStatusIpc(config, orchestrator, server, notifier, TimeProvider.System);
        var settingsIpc   = new DaemonSettingsIpc(config, orchestrator, notifier, NullLogger<DaemonSettingsIpc>.Instance);
        var restart       = RestartCoordinator.ForTest(config.Store, config.Name, "test", new NoopRestartStrategy(), TimeProvider.System);
        var control       = new LocalControlServer(config, orchestrator, restart, consentIpc, permissionIpc, statusIpc, settingsIpc,
            TestFences.Ipc(config, orchestrator), NullLogger<LocalControlServer>.Instance);
        await control.StartAsync(ct);

        var sockPath = config.Store.SocketPath(config.Name);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(sockPath) && DateTime.UtcNow < deadline) await Task.Delay(20, ct);

        return new Harness(control, orchestrator, config, sockPath);
    }

    static async Task RunAsync(ServerConnection server, Func<Harness, CancellationToken, Task> body) {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Harness? h = null;
        try {
            h = await StartAsync(server, cts.Token);
            await body(h, cts.Token);
        } finally {
            if (h is not null) {
                await h.Orchestrator.DisposeAsync();
                await h.Server.StopAsync(CancellationToken.None);
                h.Server.Dispose();
            }
        }
    }

    static async Task<NetworkStream> ConnectAsync(string sockPath, CancellationToken ct) {
        var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await sock.ConnectAsync(new UnixDomainSocketEndPoint(sockPath), ct);
        return new NetworkStream(sock, ownsSocket: true);
    }

    static async Task<AdmissionFenceAckDto> SendAsync(Stream s, LocalFrame frame, CancellationToken ct) {
        await FrameCodec.WriteAsync(s, frame, ct);
        var reply = await FrameCodec.ReadAsync(s, ct);
        await Assert.That(reply!.Type).IsEqualTo(FrameType.AdmissionFenceAck);
        return JsonSerializer.Deserialize(reply.Text, AdmissionFenceIpcJsonContext.Default.AdmissionFenceAckDto)!;
    }

    static LocalFrame AcquireFrame(string? name) =>
        LocalFrame.FenceJson(FrameType.AdmissionFenceAcquire,
            JsonSerializer.Serialize(new AdmissionFenceAcquireDto(name), AdmissionFenceIpcJsonContext.Default.AdmissionFenceAcquireDto));

    static Task WaitUnfencedAsync(AgentOrchestrator o) =>
        WaitHarness.SpinUntilAsync(() => !o.Admission.IsFenced, TimeSpan.FromSeconds(10));

    static LaunchAgentCommand Launch(AgentOrchestrator o, string id) => new(
        AgentId: id, Prompt: "hi", Model: "opus", Effort: null,
        RepoPath: "/tmp/does-not-matter", Tools: null, AttachmentIds: null, Vendor: "claude",
        Epoch: o.DaemonEpochForTest, Seq: 1, CommandId: $"cmd-{id}");

    [Test]
    public async Task Acquire_names_the_process_that_holds_it() {
        await RunAsync(new CaptureServerConnection(), async (h, ct) => {
            await using var s = await ConnectAsync(h.SockPath, ct);

            var ack = await SendAsync(s, AcquireFrame(h.Config.Name), ct);

            await Assert.That(ack).IsEqualTo(new AdmissionFenceAckDto(true, null, AdmissionFenceWire.Held, Environment.ProcessId, h.Config.InstanceId));
            await Assert.That(h.Orchestrator.Admission.IsFenced).IsTrue();
        });
    }

    [Test]
    [Arguments("other-daemon", "identity_mismatch")]
    [Arguments(null, "identity_mismatch")]
    public async Task A_fence_for_another_name_is_refused_and_takes_nothing(string? name, string reason) {
        await RunAsync(new CaptureServerConnection(), async (h, ct) => {
            await using var s = await ConnectAsync(h.SockPath, ct);

            var ack = await SendAsync(s, AcquireFrame(name), ct);

            await Assert.That(ack.Ok).IsFalse();
            await Assert.That(ack.Reason).IsEqualTo(reason);
            await Assert.That(h.Orchestrator.Admission.IsFenced).IsFalse();
        });
    }

    [Test]
    public async Task A_malformed_acquire_is_refused() {
        await RunAsync(new CaptureServerConnection(), async (h, ct) => {
            await using var s = await ConnectAsync(h.SockPath, ct);

            var ack = await SendAsync(s, LocalFrame.FenceJson(FrameType.AdmissionFenceAcquire, "not json"), ct);

            await Assert.That(ack.Reason).IsEqualTo(AdmissionFenceWire.Malformed);
            await Assert.That(h.Orchestrator.Admission.IsFenced).IsFalse();
        });
    }

    [Test]
    public async Task A_daemon_running_an_agent_refuses_the_fence() {
        await RunAsync(new CaptureServerConnection(), async (h, ct) => {
            h.Orchestrator.SeedAgentForTest("running");
            await using var s = await ConnectAsync(h.SockPath, ct);

            var ack = await SendAsync(s, AcquireFrame(h.Config.Name), ct);

            await Assert.That(ack.Reason).IsEqualTo(AdmissionFenceWire.Busy);
            await Assert.That(h.Orchestrator.Admission.IsFenced).IsFalse();
        });
    }

    [Test]
    public async Task Closing_the_connection_before_commit_releases_the_fence() {
        await RunAsync(new CaptureServerConnection(), async (h, ct) => {
            var s = await ConnectAsync(h.SockPath, ct);
            await SendAsync(s, AcquireFrame(h.Config.Name), ct);

            await s.DisposeAsync();

            await WaitUnfencedAsync(h.Orchestrator);
            await Assert.That(File.Exists(h.Config.Store.RetiringMarkerPath(h.Config.Name))).IsFalse();
        });
    }

    /// <summary>What a closed connection does to a committed fence is pinned in
    /// <see cref="AdmissionFenceTests"/>; this pins that commit is answered only once the marker exists.</summary>
    [Test]
    public async Task Commit_is_answered_once_the_marker_is_on_disk() {
        await RunAsync(new CaptureServerConnection(), async (h, ct) => {
            await using var s = await ConnectAsync(h.SockPath, ct);
            await SendAsync(s, AcquireFrame(h.Config.Name), ct);

            var committed = await SendAsync(s, new LocalFrame(FrameType.AdmissionFenceCommit), ct);

            await Assert.That(committed.State).IsEqualTo(AdmissionFenceWire.Committed);
            await Assert.That(File.Exists(h.Config.Store.RetiringMarkerPath(h.Config.Name))).IsTrue();
        });
    }

    [Test]
    public async Task Abort_after_commit_lifts_the_fence() {
        await RunAsync(new CaptureServerConnection(), async (h, ct) => {
            await using var s = await ConnectAsync(h.SockPath, ct);
            await SendAsync(s, AcquireFrame(h.Config.Name), ct);
            await SendAsync(s, new LocalFrame(FrameType.AdmissionFenceCommit), ct);

            var aborted = await SendAsync(s, new LocalFrame(FrameType.AdmissionFenceAbort), ct);

            await Assert.That(aborted.State).IsEqualTo(AdmissionFenceWire.Aborted);
            await Assert.That(h.Orchestrator.Admission.IsFenced).IsFalse();
            await Assert.That(File.Exists(h.Config.Store.RetiringMarkerPath(h.Config.Name))).IsFalse();
        });
    }

    [Test]
    public async Task A_server_launch_is_refused_while_the_fence_stands_and_admitted_after() {
        var server = new SeqCaptureServerConnection();
        await RunAsync(server, async (h, ct) => {
            var s = await ConnectAsync(h.SockPath, ct);
            await SendAsync(s, AcquireFrame(h.Config.Name), ct);

            await h.Orchestrator.HandleLaunchAgentForTest(Launch(h.Orchestrator, "fenced"));
            await WaitHarness.SpinUntilAsync(() => server.Rejects.Count > 0, TimeSpan.FromSeconds(10));

            await Assert.That(server.Rejects.Single().Reason).IsEqualTo(CommandRejectedReason.Semantic);
            await Assert.That(server.LaunchFaileds.Single().Reason).StartsWith(AdmissionFenceWire.RetiringReasonPrefix);

            await s.DisposeAsync();
            await WaitUnfencedAsync(h.Orchestrator);
        });
    }

    [Test]
    public async Task A_local_spawn_is_refused_while_the_fence_stands() {
        await RunAsync(new CaptureServerConnection(), async (h, ct) => {
            await using var s = await ConnectAsync(h.SockPath, ct);
            await SendAsync(s, AcquireFrame(h.Config.Name), ct);

            using var reply = new MemoryStream();
            var spawn = FrameCodec.Spawn("claude", WorkLocation.BorrowedCwd, false, h.Config.Store.StateDirectory(h.Config.Name), [], 80, 24);
            await h.Orchestrator.HandleLocalSpawnAsync(spawn, reply, ct);

            reply.Position = 0;
            var frame = await FrameCodec.ReadAsync(reply, ct);
            await Assert.That(frame!.Type).IsEqualTo(FrameType.Error);
            await Assert.That(frame.Text).Contains("renamed");
            await Assert.That(h.Orchestrator.ActiveCount).IsEqualTo(0);
        });
    }

    [Test]
    public async Task Hello_advertises_fence_1() {
        await RunAsync(new CaptureServerConnection(), async (h, ct) => {
            await using var s = await ConnectAsync(h.SockPath, ct);
            await FrameCodec.WriteAsync(s, new LocalFrame(FrameType.Hello), ct);
            var reply = await FrameCodec.ReadAsync(s, ct);
            var dto = JsonSerializer.Deserialize(reply!.Text, HelloIpcJsonContext.Default.HelloReplyDto)!;

            await Assert.That(dto.Capabilities).Contains(AdmissionFenceWire.Capability);
        });
    }

    [Test]
    public async Task The_core_client_acquires_commits_and_aborts_a_fence() {
        await RunAsync(new CaptureServerConnection(), async (h, ct) => {
            var result = await AdmissionFenceClient.AcquireAsync(h.Config.Store, h.Config.Name, TimeSpan.FromSeconds(5), TimeProvider.System, ct);

            await Assert.That(result.Outcome).IsEqualTo(AdmissionFenceOutcome.Acquired);
            await using var session = result.Session!;
            await Assert.That(session.Pid).IsEqualTo(Environment.ProcessId);
            await Assert.That(h.Orchestrator.Admission.IsFenced).IsTrue();

            await Assert.That(await session.CommitAsync(TimeSpan.FromSeconds(5), ct)).IsTrue();
            await Assert.That(File.Exists(h.Config.Store.RetiringMarkerPath(h.Config.Name))).IsTrue();

            await Assert.That(await session.AbortAsync(TimeSpan.FromSeconds(5), ct)).IsTrue();
            await Assert.That(h.Orchestrator.Admission.IsFenced).IsFalse();
        });
    }

    [Test]
    public async Task The_core_client_reports_a_busy_daemon() {
        await RunAsync(new CaptureServerConnection(), async (h, ct) => {
            h.Orchestrator.SeedAgentForTest("running");

            var result = await AdmissionFenceClient.AcquireAsync(h.Config.Store, h.Config.Name, TimeSpan.FromSeconds(5), TimeProvider.System, ct);

            await Assert.That(result.Outcome).IsEqualTo(AdmissionFenceOutcome.Busy);
            await Assert.That(result.Session).IsNull();
        });
    }

    [Test]
    public async Task The_core_client_reports_an_absent_daemon_as_unavailable() {
        await RunAsync(new CaptureServerConnection(), async (h, ct) => {
            var result = await AdmissionFenceClient.AcquireAsync(h.Config.Store, "no-such-daemon", TimeSpan.FromSeconds(2), TimeProvider.System, ct);

            await Assert.That(result.Outcome).IsEqualTo(AdmissionFenceOutcome.Unavailable);
        });
    }

    [Test]
    public async Task Disposing_the_core_session_before_commit_releases_the_fence() {
        await RunAsync(new CaptureServerConnection(), async (h, ct) => {
            var result = await AdmissionFenceClient.AcquireAsync(h.Config.Store, h.Config.Name, TimeSpan.FromSeconds(5), TimeProvider.System, ct);
            await Assert.That(h.Orchestrator.Admission.IsFenced).IsTrue();

            await result.Session!.DisposeAsync();

            await WaitUnfencedAsync(h.Orchestrator);
        });
    }

    [Test]
    public async Task A_second_rename_is_told_the_daemon_is_already_fenced() {
        await RunAsync(new CaptureServerConnection(), async (h, ct) => {
            await using var first = await ConnectAsync(h.SockPath, ct);
            await SendAsync(first, AcquireFrame(h.Config.Name), ct);
            await using var second = await ConnectAsync(h.SockPath, ct);

            var ack = await SendAsync(second, AcquireFrame(h.Config.Name), ct);

            await Assert.That(ack.Reason).IsEqualTo(AdmissionFenceWire.Fenced);
        });
    }
}
