using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Resume;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// The orchestrator keeps one resume-ledger entry per interactive agent for as long as the agent
/// lives, so a successor daemon has a launch to resume; a normal end removes it.
/// </summary>
public class AgentOrchestratorResumeLedgerTests {
    sealed class ObservingAcpFactory : IHostedAgentRuntimeFactory {
        readonly SpyAcpHostedAgentRuntimeFactory _inner = new();
        public string CliPath => _inner.CliPath;
        public string Vendor => _inner.Vendor;
        public bool SupportsUnattended => false;
        public Func<ResumeLedgerEntry?>? Observe { get; set; }
        public ResumeLedgerEntry? ObservedAtStart { get; private set; }
        public bool IsAvailable() => true;

        public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
            ObservedAtStart = Observe?.Invoke();
            return _inner.StartAsync(ctx, ct);
        }
    }

    sealed class ObservingThenFailingFactory : IHostedAgentRuntimeFactory {
        public string CliPath => "x";
        public string Vendor => "cursor";
        public bool SupportsUnattended => false;
        public Func<ResumeLedgerEntry?>? Observe { get; set; }
        public ResumeLedgerEntry? ObservedAtStart { get; private set; }
        public bool IsAvailable() => true;

        public Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
            ObservedAtStart = Observe?.Invoke();
            throw new InvalidOperationException("boom");
        }
    }

    [Test]
    public async Task Admitted_launch_writes_a_running_ledger_entry_before_the_runtime_starts() {
        using var repoPath = GitRepo.CreateWithCommit();
        var factory = new ObservingAcpFactory();
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            allowedRepoPath: repoPath, extraRuntimeFactories: [factory]);
        factory.Observe = () => orch.ResumeLedger!.TryRead("ledger-launch");

        await orch.HandleLaunchAgentForTest(AgentOrchestratorHarness.NewCursorLaunch("ledger-launch", repoPath));

        var atStart = factory.ObservedAtStart;
        await Assert.That(atStart).IsNotNull();
        await Assert.That(atStart!.State).IsEqualTo(ResumeState.Running);
        await Assert.That(atStart.Vendor).IsEqualTo("cursor");
        await Assert.That(atStart.Launch.Origin).IsEqualTo("server");
        await Assert.That(atStart.Launch.ConsentSource).IsEqualTo("default");
        await Assert.That(atStart.Launch.ConsentAt).IsNotNull();
        await Assert.That(atStart.Launch.Prompt).IsEqualTo("do work");
        await Assert.That(atStart.Launch.RepoPath).IsEqualTo((string)repoPath);

        var agent = orch.GetAgentForTest("ledger-launch")!;
        await Assert.That(atStart.Launch.WorktreePath).IsEqualTo(agent.Worktree.Path);
        var after = orch.ResumeLedger!.TryRead("ledger-launch")!;
        await Assert.That(after.SessionId).IsEqualTo(agent.SessionId);
        await Assert.That(after.SessionId).IsNotNull();
        await Assert.That(after.Transport).IsEqualTo(agent.RuntimeTransport);
        await Assert.That(after.Launch.ConsentSource).IsEqualTo("default");
        await Assert.That(after.Launch.ConsentAt).IsEqualTo(atStart.Launch.ConsentAt);
        await Assert.That(after.CreatedAt).IsEqualTo(atStart.CreatedAt);
    }

    [Test]
    public async Task A_launch_that_fails_to_start_leaves_no_entry() {
        using var repoPath = GitRepo.CreateWithCommit();
        var factory = new ObservingThenFailingFactory();
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            allowedRepoPath: repoPath, extraRuntimeFactories: [factory]);
        factory.Observe = () => orch.ResumeLedger!.TryRead("ledger-fail");

        await orch.HandleLaunchAgentForTest(AgentOrchestratorHarness.NewCursorLaunch("ledger-fail", repoPath));

        await Assert.That(factory.ObservedAtStart).IsNotNull();
        await Assert.That(orch.ResumeLedger!.TryRead("ledger-fail")).IsNull();
    }

    [Test]
    public async Task Learning_the_session_id_rewrites_the_entry() {
        await using var orch = Build();
        var agent = orch.SeedAgentForTest("ledger-session");
        orch.WriteLedger(agent, cmd: null, origin: "local", consent: null);

        await orch.RunDiscoveryForTest(agent, _ => ("0123456789abcdef0123456789abcdef", "/t.jsonl"));

        var entry = orch.ResumeLedger!.TryRead("ledger-session");
        await Assert.That(entry).IsNotNull();
        await Assert.That(entry!.SessionId).IsEqualTo("0123456789abcdef0123456789abcdef");
        await Assert.That(entry.VendorSession).IsEqualTo("0123456789abcdef0123456789abcdef");
    }

    [Test]
    public async Task Normal_end_deletes_the_entry() {
        await using var orch = Build();
        var agent = orch.SeedAgentForTest("ledger-end");
        orch.WriteLedger(agent, cmd: null, origin: "local", consent: null);
        await Assert.That(orch.ResumeLedger!.TryRead("ledger-end")).IsNotNull();

        await orch.CleanupAgentForTest("ledger-end");

        await Assert.That(orch.ResumeLedger!.TryRead("ledger-end")).IsNull();
    }

    [Test]
    public async Task A_pending_resume_keeps_the_entry_and_the_pid_record() {
        await using var orch = Build();
        var agent = orch.SeedAgentForTest("ledger-pending");
        orch.WriteLedger(agent, cmd: null, origin: "local", consent: null);
        orch.WritePidRecordForTest(new AgentPidRecord(
            "ledger-pending", 999_999, "identity", PidIdentityKind.Present, "Default", "codex", null, null,
            orch.DaemonIdForTest, orch.DaemonEpochForTest, TimeProvider.System.GetUtcNow()));
        agent.ResumePending = true;

        await orch.CleanupAgentForTest("ledger-pending");

        await Assert.That(orch.ResumeLedger!.TryRead("ledger-pending")).IsNotNull();
        await Assert.That(orch.PidRecordsForTest().Any(r => r.AgentId == "ledger-pending")).IsTrue();
    }

    [Test]
    public async Task A_flow_participant_writes_no_entry() {
        await using var orch = Build();
        var agent = orch.SeedAgentForTest("ledger-flow", LaunchKind.ReviewFlow, flowRunId: "run-1", flowRole: "reviewer");

        orch.WriteLedger(agent, cmd: null, origin: "server", consent: null);
        agent.SessionId = "sess";
        orch.UpdateLedgerSession(agent);

        await Assert.That(orch.ResumeLedger!.ReadAll()).IsEmpty();
    }

    [Test]
    public async Task A_local_spawn_writes_a_local_entry_without_consent() {
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new FixedPtyProcessFactory(new OneChunkThenBlockPtyProcess()),
            AgentOrchestratorHarness.Launcher("claude"));
        using var cwd = new TempDir();

        var readBuf = new MemoryStream();
        await FrameCodec.WriteAsync(readBuf, LocalFrame.Detach(), default);
        readBuf.Position = 0;
        using var client = new DuplexTestStream(readBuf, new MemoryStream());
        await orch.HandleLocalSpawnAsync(
            FrameCodec.Spawn("claude", WorkLocation.BorrowedCwd, isPrivate: true, cwd.Path, [], 80, 24), client, default);

        var entry = orch.ResumeLedger!.ReadAll().Single();
        await Assert.That(entry.Launch.Origin).IsEqualTo("local");
        await Assert.That(entry.Launch.ConsentSource).IsNull();
        await Assert.That(entry.Launch.Work).IsEqualTo(WorkLocation.BorrowedCwd);
        await Assert.That(entry.Launch.IsPrivate).IsTrue();
    }

    static AgentOrchestrator Build() => AgentOrchestratorHarness.BuildOrchestrator(
        new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
}
