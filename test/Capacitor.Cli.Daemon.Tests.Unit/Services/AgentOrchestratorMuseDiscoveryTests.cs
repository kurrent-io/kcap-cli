using Capacitor.Cli.Core.Harness.Muse;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class AgentOrchestratorMuseDiscoveryTests {
    const string SessionId = "01a13695-0736-75d0-8f4e-f121585b8453";

    /// <summary>A Muse runtime carries no transcript, so the session id comes from the hosted-session
    /// marker the factory wrote, once Muse has created the session's log.</summary>
    [Test]
    public async Task A_hosted_muse_agent_is_linked_to_the_session_its_marker_names() {
        using var repoPath = GitRepo.CreateWithCommit();
        var server = new CaptureServerConnection();
        DaemonConfig? config = null;
        var factory = new MarkingMuseFactory(() => config!);

        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            allowedRepoPath: repoPath, extraRuntimeFactories: [factory], configure: c => config = c);

        await orch.HandleLaunchAgentForTest(AgentOrchestratorHarness.NewCursorLaunch("agent-muse", repoPath) with { Vendor = "muse" });
        await WaitHarness.PollUntilAsync(() => orch.SnapshotAgentsForStatus().Single().SessionId is not null);

        var row = orch.SnapshotAgentsForStatus().Single();
        await Assert.That(row.SessionId).IsEqualTo(SessionId.Replace("-", ""));
        await Assert.That(row.TranscriptPath).IsEqualTo(factory.LogPath);
        await Assert.That(orch.DiscoveryStartsForTest).IsEqualTo(1);
    }

    sealed class MarkingMuseFactory(Func<DaemonConfig> config) : IHostedAgentRuntimeFactory {
        public string  Vendor             => "muse";
        public string  CliPath            => "muse";
        public bool    SupportsUnattended => false;
        public string? LogPath            { get; private set; }

        public bool IsAvailable() => true;

        public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
            var cfg = config();
            MuseHostedSessionMarker.Write(cfg.Home, new(SessionId, ctx.AgentId, ctx.DaemonId, ctx.DaemonEpoch, TimeProvider.System.GetUtcNow()));

            LogPath = new MusePaths(cfg.Home, null, null).SessionLogPath(SessionId, DateOnly.FromDateTime(TimeProvider.System.GetLocalNow().DateTime))!;
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.WriteAllText(LogPath, "");

            return Task.FromResult(new HostedRuntimeStart(new FakeHostedAgentRuntime("muse", emitsTerminalOutput: false), McpConfigPath: null, Transcript: null));
        }
    }
}
