using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Daemon.Harness.Codex;
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

    [Test]
    public async Task Cleanup_completes_the_journal_after_disposing_the_runtime() {
        using var repoPath = GitRepo.CreateWithCommit();
        var server = new CaptureServerConnection();
        var factory = new OpeningAcpFactory();
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            allowedRepoPath: repoPath, extraRuntimeFactories: [factory]);
        orch.GracefulExitWait = TimeSpan.FromMilliseconds(50);
        await orch.HandleLaunchAgentForTest(AgentOrchestratorHarness.NewCursorLaunch("agent-cleanup", repoPath));
        var journal = factory.LastContext!.Journal!;
        journal.Record(new AcpEventEnvelope(Kind: AcpEventKind.AssistantText, Text: "bye"));

        await orch.HandleLocalStopV2Async(force: true, "agent-cleanup", Stream.Null, CancellationToken.None);
        await WaitUntil(() => journal.Drained);

        await Assert.That(File.Exists(journal.Path)).IsTrue(); // agent exit never deletes a journal
        await Assert.That(File.ReadAllText(journal.Path)).Contains("\"bye\"");
    }

    [Test]
    public async Task Factory_failure_after_open_deletes_only_a_journal_this_launch_created() {
        using var repoPath = GitRepo.CreateWithCommit();
        var server = new CaptureServerConnection();
        var factory = new FailingAfterOpenFactory();
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            allowedRepoPath: repoPath, extraRuntimeFactories: [factory]);

        await orch.HandleLaunchAgentForTest(AgentOrchestratorHarness.NewCursorLaunch("agent-fail-fresh", repoPath));
        await Assert.That(File.Exists(factory.LastJournal!.Path)).IsFalse();

        // A pre-existing file (a rebind whose factory fails) keeps the prior incarnation's bytes.
        var existing = TranscriptJournal.ForAgent(orch.PidRecordRootForTest, "agent-fail-rebind", Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        existing.Open("/w", null); await existing.CompleteAsync();
        await orch.HandleLaunchAgentForTest(AgentOrchestratorHarness.NewCursorLaunch("agent-fail-rebind", repoPath));
        await Assert.That(File.Exists(existing.Path)).IsTrue();
        await Assert.That(File.ReadLines(existing.Path).Count()).IsEqualTo(2); // its header plus the failed launch's header
    }

    sealed class FailingAfterOpenFactory : IHostedAgentRuntimeFactory {
        public string CliPath => "x"; public string Vendor => "cursor"; public bool SupportsUnattended => false;
        public TranscriptJournal? LastJournal { get; private set; }
        public bool IsAvailable() => true;
        public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
            LastJournal = ctx.Journal;
            ctx.Journal?.Open(ctx.Worktree.Path, ctx.Model);
            throw new InvalidOperationException("boom");
        }
    }

    [Test]
    public async Task Codex_preflight_failure_after_open_discards_the_journal() {
        using var repoPath = GitRepo.CreateWithCommit();
        var server = new CaptureServerConnection();
        var factory = new FailingWithCodexPreflightFactory();
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            allowedRepoPath: repoPath, extraRuntimeFactories: [factory]);

        await orch.HandleLaunchAgentForTest(AgentOrchestratorHarness.NewCursorLaunch("agent-codex-preflight", repoPath));

        var journal = factory.LastJournal!;
        await Assert.That(File.Exists(journal.Path)).IsFalse();
        await Assert.That(journal.Drained).IsTrue();
    }

    /// The inner Codex-preflight catch has its own return, before the outer catch's journal
    /// cleanup — this pins that arm discards the journal too. Vendor "cursor" is fine here: the
    /// catch filters on exception type, not vendor.
    sealed class FailingWithCodexPreflightFactory : IHostedAgentRuntimeFactory {
        public string CliPath => "x"; public string Vendor => "cursor"; public bool SupportsUnattended => false;
        public TranscriptJournal? LastJournal { get; private set; }
        public bool IsAvailable() => true;
        public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
            LastJournal = ctx.Journal;
            ctx.Journal?.Open(ctx.Worktree.Path, ctx.Model);
            throw new CodexHooksNotInstalledException("Run plugin install --codex");
        }
    }

    static async Task WaitUntil(Func<bool> condition) {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        await Assert.That(condition()).IsTrue();
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
