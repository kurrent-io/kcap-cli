using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class AgentOrchestratorJournalTests {
    [Test]
    public async Task Launch_hands_the_factory_an_unopened_journal_and_publishes_the_opened_path_before_registration() {
        using var repoPath = GitRepo.CreateWithCommit();
        var server = new CaptureServerConnection();
        var factory = new OpeningAcpFactory();
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            allowedRepoPath: repoPath, extraRuntimeFactories: [factory]);

        await orch.HandleLaunchAgentForTest(AgentOrchestratorHarness.NewCursorLaunch("agent-journal", repoPath));

        var ctx = factory.LastContext!;
        await Assert.That(ctx.Journal).IsNotNull();
        await Assert.That(ctx.Journal!.Path).IsEqualTo(Path.Combine(orch.PidRecordRootForTest, "transcripts", AgentFileNames.For("agent-journal") + ".jsonl"));
        var row = orch.SnapshotAgentsForStatus().Single();
        await Assert.That(row.TranscriptPath).IsEqualTo(ctx.Journal.Path);
        await Assert.That(row.TranscriptFormat).IsEqualTo(TranscriptFormats.Envelopes);
        await Assert.That(row.SessionId).IsEqualTo("acp-sess-1");
        await Assert.That(File.Exists(ctx.Journal.Path)).IsTrue();
        // The first status the server saw for this agent already carried the canonical id.
        await Assert.That(server.StatusChangedWithSession.First(c => c.AgentId == "agent-journal").SessionId).IsEqualTo("acp-sess-1");
        // Discovery never started for an envelope-sourced agent.
        await Assert.That(orch.DiscoveryStartsForTest).IsEqualTo(0);
    }

    [Test]
    public async Task Envelope_sourced_codex_keeps_the_journal_path_and_arms_no_probe() {
        using var repoPath = GitRepo.CreateWithCommit();
        var server = new CaptureServerConnection();
        var factory = new OpeningAcpFactory("codex");
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            allowedRepoPath: repoPath, extraRuntimeFactories: [factory]);

        await orch.HandleLaunchAgentForTest(AgentOrchestratorHarness.NewCursorLaunch("agent-codex-env", repoPath) with { Vendor = "codex" });
        await orch.HandleSendInputForTest(new SendInputCommand("agent-codex-env", "hello", null));

        await Assert.That(orch.SnapshotAgentsForStatus().Single().TranscriptPath).IsEqualTo(factory.LastContext!.Journal!.Path);
        await Assert.That(orch.DiscoveryStartsForTest).IsEqualTo(0);
        await Assert.That(orch.CodexProbesArmedForTest).IsEqualTo(0);
    }

    /// The real factories call Open before constructing the runtime; this double does the same.
    sealed class OpeningAcpFactory(string vendor = "cursor") : IHostedAgentRuntimeFactory {
        readonly SpyAcpHostedAgentRuntimeFactory _inner = new(vendor);
        public string CliPath => _inner.CliPath;
        public string Vendor => _inner.Vendor;
        public bool SupportsUnattended => false;
        public RuntimeStartContext? LastContext => _inner.LastContext;
        public FakeAcpRuntime? LastRuntime => _inner.LastRuntime;
        public bool IsAvailable() => true;
        public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
            ctx.Journal?.Open(ctx.Worktree.Path, ctx.Model);
            return _inner.StartAsync(ctx, ct);
        }
    }
}
