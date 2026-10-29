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
    sealed class ObservingAcpFactory(string vendor = "cursor") : IHostedAgentRuntimeFactory {
        readonly SpyAcpHostedAgentRuntimeFactory _inner = new(vendor);
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
            allowedRepoPath: repoPath, extraRuntimeFactories: [factory], configure: c => c.InstanceId = "instance-now");
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
        await Assert.That(atStart.Launch.WorktreeBranch).IsEqualTo(agent.Worktree.Branch);
        await Assert.That(atStart.Launch.WorktreeIsStandalone).IsEqualTo(agent.Worktree.IsStandalone);
        await Assert.That(atStart.Launch.WorktreeSourceRepo).IsEqualTo(agent.Worktree.SourceRepo);
        await Assert.That(atStart.DaemonInstanceId).IsEqualTo("instance-now");
        var after = orch.ResumeLedger!.TryRead("ledger-launch")!;
        await Assert.That(after.SessionId).IsEqualTo(agent.SessionId);
        await Assert.That(after.SessionId).IsNotNull();
        await Assert.That(after.Transport).IsEqualTo(agent.RuntimeTransport);
        await Assert.That(after.HasTerminal).IsEqualTo(agent.Runtime.EmitsTerminalOutput);
        await Assert.That(after.TranscriptFormat).IsEqualTo(AgentOrchestrator.TranscriptFormatOf(agent));
        await Assert.That(after.StartedAt?.UtcDateTime).IsEqualTo(agent.CreatedAt);
        await Assert.That(after.Launch.ConsentSource).IsEqualTo("default");
        await Assert.That(after.Launch.ConsentAt).IsEqualTo(atStart.Launch.ConsentAt);
        await Assert.That(after.CreatedAt).IsEqualTo(atStart.CreatedAt);
        await Assert.That(after.Launch.WorktreeBranch).IsEqualTo(agent.Worktree.Branch);
        await Assert.That(after.DaemonInstanceId).IsEqualTo("instance-now");
    }

    /// <summary>A resume relaunches with the launch's permission mode, so the pre-start entry and its
    /// rewrite both carry it.</summary>
    [Test]
    public async Task Both_ledger_writes_record_the_claude_permission_mode() {
        using var repoPath = GitRepo.CreateWithCommit();
        var factory = new ObservingAcpFactory("claude");
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            allowedRepoPath: repoPath, extraRuntimeFactories: [factory]);
        factory.Observe = () => orch.ResumeLedger!.TryRead("ledger-mode");

        await orch.HandleLaunchAgentForTest(
            AgentOrchestratorHarness.NewCursorLaunch("ledger-mode", repoPath) with { Vendor = "claude", PermissionMode = "acceptEdits" });

        await Assert.That(orch.GetAgentForTest("ledger-mode")).IsNotNull();
        await Assert.That(factory.ObservedAtStart!.Launch.PermissionMode).IsEqualTo("acceptEdits");
        await Assert.That(orch.ResumeLedger!.TryRead("ledger-mode")!.Launch.PermissionMode).IsEqualTo("acceptEdits");
    }

    [Test]
    [Arguments(null, true)]
    [Arguments("Named by the caller", false)]
    public async Task Both_ledger_writes_record_whether_the_start_title_was_cut_from_the_prompt(string? title, bool derived) {
        using var repoPath = GitRepo.CreateWithCommit();
        var factory = new ObservingAcpFactory();
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            allowedRepoPath: repoPath, extraRuntimeFactories: [factory]);
        factory.Observe = () => orch.ResumeLedger!.TryRead("ledger-title");

        await orch.HandleLaunchAgentForTest(
            AgentOrchestratorHarness.NewCursorLaunch("ledger-title", repoPath) with { Title = title });

        await Assert.That(orch.GetAgentForTest("ledger-title")).IsNotNull();
        await Assert.That(factory.ObservedAtStart!.Launch.TitleDerived).IsEqualTo(derived);
        await Assert.That(orch.ResumeLedger!.TryRead("ledger-title")!.Launch.TitleDerived).IsEqualTo(derived);
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
        var launchers = AgentOrchestratorHarness.Launcher("codex");
        var launcher  = (SpyHostedAgentLauncher)launchers["codex"];
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), launchers);
        using var tmp = new TempDir();
        var worktree  = tmp.CreateDir("worktree");
        var mcpConfig = tmp.CreateFile("mcp.json", "{}");
        var agent = orch.SeedAgentForTest("ledger-pending", worktree: new WorktreeInfo(worktree, "b", "/repo"));
        agent.McpConfigPath = mcpConfig;
        orch.WriteLedger(agent, cmd: null, origin: "local", consent: null);
        orch.WritePidRecordForTest(new AgentPidRecord(
            "ledger-pending", 999_999, "identity", PidIdentityKind.Present, "Default", "codex", null, null,
            orch.DaemonIdForTest, orch.DaemonEpochForTest, TimeProvider.System.GetUtcNow()));
        agent.ResumePending = true;

        await orch.CleanupAgentForTest("ledger-pending");

        await Assert.That(orch.ResumeLedger!.TryRead("ledger-pending")).IsNotNull();
        await Assert.That(orch.PidRecordsForTest().Any(r => r.AgentId == "ledger-pending")).IsTrue();
        await Assert.That(Directory.Exists(worktree)).IsTrue();
        await Assert.That(File.Exists(mcpConfig)).IsTrue();
        await Assert.That(launcher.CleanupCalls).IsEqualTo(0);
    }

    /// The directory is left read-only, so the write fails with an access error rather than an
    /// IOException; the launch must still complete.
    [Test]
    public async Task An_unwritable_ledger_does_not_fail_the_launch() {
        if (OperatingSystem.IsWindows()) return;

        using var repoPath = GitRepo.CreateWithCommit();
        var logger = new CapturingLogger<AgentOrchestrator>();
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            allowedRepoPath: repoPath, extraRuntimeFactories: [new ObservingAcpFactory()], logger: logger);
        var resumeDir = Path.GetDirectoryName(orch.ResumeLedger!.EntryPath("ledger-denied"))!;
        Directory.CreateDirectory(resumeDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try {
            await orch.HandleLaunchAgentForTest(AgentOrchestratorHarness.NewCursorLaunch("ledger-denied", repoPath));

            await Assert.That(orch.GetAgentForTest("ledger-denied")).IsNotNull();
            await Assert.That(orch.ResumeLedger!.TryRead("ledger-denied")).IsNull();
            await Assert.That(logger.Entries.Any(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning
                                                   && e.Message.Contains("Resume ledger")
                                                   && e.Message.Contains("ledger-denied"))).IsTrue();
        } finally {
            File.SetUnixFileMode(resumeDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
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

    [Test]
    public async Task A_claude_launch_keeps_its_unconfirmed_pin_only_as_the_vendor_session() {
        using var repoPath = GitRepo.CreateWithCommit();
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), AgentOrchestratorHarness.Launcher("claude"),
            allowedRepoPath: repoPath);

        await orch.HandleLaunchAgentForTest(AgentOrchestratorHarness.NewCursorLaunch("pin-launch", repoPath) with { Vendor = "claude" });

        var agent = orch.GetAgentForTest("pin-launch")!;
        var entry = orch.ResumeLedger!.TryRead("pin-launch")!;
        await Assert.That(Guid.TryParse(entry.VendorSession, out _)).IsTrue();
        await Assert.That(entry.SessionId).IsNull();
        await Assert.That(agent.VendorSession).IsEqualTo(entry.VendorSession);
        await Assert.That(agent.SessionId).IsNull();
    }

    static AgentOrchestrator Build() => AgentOrchestratorHarness.BuildOrchestrator(
        new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
}
