using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Resume;
using Capacitor.Cli.Daemon.Pty;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// An agent running in place shares its folder, and with it Claude's project dir and the transcripts'
/// cwd, with every other Claude session there. Each test writes such a session after the agent spawns,
/// so a cwd match would take it: discovery must link the agent's own transcript or none.
/// </summary>
[ParallelLimiter<SubprocessLimit>]
public class AgentOrchestratorClaudeDiscoveryTests {
    const string Own     = "0a0a0a0a-1111-4111-8111-000000000001";
    const string Foreign = "0f0f0f0f-2222-4222-8222-000000000002";

    [TempDir] public required TempDir Tmp { get; init; }

    readonly SpyHostedAgentLauncher _launcher = new("claude", cliPath: "spy-claude") { SupportsResume = true, PassthroughSessionId = Own };

    UserHome? _home;

    AgentOrchestrator Build(IPtyProcess pty, string? allowedRepo = null, CaptureServerConnection? server = null) =>
        AgentOrchestratorHarness.BuildOrchestrator(
            server ?? new CaptureServerConnection(), new SpyPtyProcessFactory(pty), new Dictionary<string, IHostedAgentLauncher> { [_launcher.Vendor] = _launcher },
            allowedRepoPath: allowedRepo,
            configure: c => {
                c.InstanceId            = "instance-now";
                c.ResumeReadinessWindow = TimeSpan.FromMilliseconds(100);
                _home                   = c.Home;
            });

    string Stage(string cwd, string sessionId) =>
        new TempDirHandle(new ClaudePaths(_home!, null).ProjectDir(cwd))
            .CreateFile($"{sessionId}.jsonl", new JsonObject { ["type"] = "user", ["cwd"] = cwd }.ToJsonString() + "\n");

    /// <summary>Every session id the daemon reported for an agent, by heartbeat or by status.</summary>
    static List<string?> Reported(CaptureServerConnection server, string agentId) {
        List<string?> ids;
        lock (server.RunEvents) ids = [.. server.RunEvents.Where(e => e.AgentId == agentId).Select(e => (e.Event as AgentRunHeartbeat)?.SessionId)];
        lock (server.StatusChangedWithSession) ids.AddRange(server.StatusChangedWithSession.Where(c => c.AgentId == agentId).Select(c => c.SessionId));

        return [.. ids.Select(SessionIds.Canonical)];
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    public async Task An_in_place_launch_links_its_pinned_transcript_not_another_session_in_the_folder(string? resumeSessionId) {
        using var repo = GitRepo.CreateWithCommit();
        var server = new CaptureServerConnection();
        await using var orch = Build(new OneChunkThenBlockPtyProcess(), server: server);
        _launcher.OnPrepare = ctx => Stage(ctx.Worktree.Path, Foreign);

        await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
            AgentId: "in-place", Prompt: "hi", Model: "opus", Effort: null, RepoPath: repo,
            Tools: null, AttachmentIds: null, Vendor: "claude", Borrowed: true, BorrowCwd: repo,
            ResumeSessionId: resumeSessionId));

        await Assert.That(server.LaunchFailedCalls).IsEmpty();
        var agent  = orch.GetAgentForTest("in-place")!;
        var pinned = agent.VendorSession!;
        await Assert.That(Guid.TryParse(pinned, out _)).IsTrue();
        await Assert.That(agent.BridgedSinceStart).IsTrue();
        await Assert.That(agent.Work).IsEqualTo(WorkLocation.BorrowedCwd);
        await Assert.That(agent.TranscriptPath).IsNull();
        await Assert.That(agent.SessionId).IsNull();

        var own = Stage(agent.Worktree.Path, pinned);
        await WaitHarness.PollUntilAsync(() => agent.TranscriptPath is not null);

        await Assert.That(agent.TranscriptPath).IsEqualTo(own);
        await Assert.That(agent.SessionId).IsEqualTo(SessionIds.Canonical(pinned));
        await Assert.That(Reported(server, "in-place")).DoesNotContain(SessionIds.Canonical(Foreign));
    }

    [Test]
    public async Task A_server_requested_resume_in_place_links_the_requested_transcript() {
        using var repo = GitRepo.CreateWithCommit();
        var server = new CaptureServerConnection();
        await using var orch = Build(new OneChunkThenBlockPtyProcess(), server: server);
        string? own = null;
        _launcher.OnPrepare = ctx => {
            own = Stage(ctx.Worktree.Path, Own);
            Stage(ctx.Worktree.Path, Foreign);
        };

        await orch.HandleLaunchAgentForTest(new LaunchAgentCommand(
            AgentId: "server-resume", Prompt: "hi", Model: "opus", Effort: null, RepoPath: repo,
            Tools: null, AttachmentIds: null, Vendor: "claude", Borrowed: true, BorrowCwd: repo,
            ResumeSessionId: Own));

        await Assert.That(server.LaunchFailedCalls).IsEmpty();
        var agent = orch.GetAgentForTest("server-resume")!;
        await Assert.That(agent.VendorSession).IsEqualTo(Own);
        await WaitHarness.PollUntilAsync(() => agent.TranscriptPath is not null);

        await Assert.That(agent.TranscriptPath).IsEqualTo(own);
        await Assert.That(agent.SessionId).IsEqualTo(SessionIds.Canonical(Own));
        await Assert.That(orch.ResumeLedger!.TryRead(agent.Id)!.VendorSession).IsEqualTo(Own);
        await Assert.That(Reported(server, agent.Id)).DoesNotContain(SessionIds.Canonical(Foreign));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_resume_in_place_links_its_existing_transcript_not_another_one_in_the_folder(bool staleServerSession) {
        var cwd = Tmp.CreateDir("repo");
        var server = new CaptureServerConnection();
        await using var orch = Build(new ScriptedPtyProcess(), cwd, server);
        var own = Stage(cwd, Own);
        File.SetLastWriteTimeUtc(own, DateTime.UtcNow.AddDays(-2));
        _launcher.OnPrepare = ctx => Stage(ctx.Worktree.Path, Foreign);
        var entry = InPlaceEntry("resumed", cwd, Own);
        orch.ResumeLedger!.Write(staleServerSession ? entry with { SessionId = SessionIds.Canonical(Foreign) } : entry);

        orch.LoadResumeLedger();
        await orch.ResumeOnBootAsync(CancellationToken.None);

        var agent = orch.GetAgentForTest("resumed")!;
        await Assert.That(_launcher.LastContext!.ResumeSessionId).IsEqualTo(Own);
        await WaitHarness.PollUntilAsync(() => agent.TranscriptPath is not null);

        await Assert.That(agent.TranscriptPath).IsEqualTo(own);
        await Assert.That(agent.SessionId).IsEqualTo(SessionIds.Canonical(Own));
        await Assert.That(orch.ResumeLedger!.TryRead("resumed")!.VendorSession).IsEqualTo(Own);
        await Assert.That(Reported(server, "resumed")).DoesNotContain(SessionIds.Canonical(Foreign));
    }

    [Test]
    public async Task A_local_in_place_spawn_runs_under_the_session_its_launcher_pinned() {
        var cwd = Tmp.CreateDir("repo");
        var server = new CaptureServerConnection();
        await using var orch = Build(new OneChunkThenBlockPtyProcess(), server: server);
        _launcher.OnPrepare = ctx => Stage(ctx.Worktree.Path, Foreign);

        var readBuf = new MemoryStream();
        await FrameCodec.WriteAsync(readBuf, LocalFrame.Detach(), default);
        readBuf.Position = 0;
        using var client = new DuplexTestStream(readBuf, new MemoryStream());
        await orch.HandleLocalSpawnAsync(FrameCodec.Spawn("claude", WorkLocation.BorrowedCwd, isPrivate: false, cwd, [], 80, 24), client, default);

        var agent = orch.GetAgentForTest(orch.SnapshotAgentsForStatus().Single().Id)!;
        await Assert.That(agent.SessionId).IsNull();
        await Assert.That(agent.VendorSession).IsEqualTo(Own);
        await Assert.That(orch.ResumeLedger!.TryRead(agent.Id)!.VendorSession).IsEqualTo(Own);

        var own = Stage(agent.Worktree.Path, Own);
        await WaitHarness.PollUntilAsync(() => agent.TranscriptPath is not null);

        await Assert.That(agent.TranscriptPath).IsEqualTo(own);
        await Assert.That(Reported(server, agent.Id)).DoesNotContain(SessionIds.Canonical(Foreign));
    }

    /// <summary>A cwd match is only proof in a worktree created for the agent, so an agent that runs in place
    /// with no session id looks for no transcript at all.</summary>
    [Test]
    public async Task An_in_place_agent_with_no_session_id_discovers_nothing() {
        var cwd = Tmp.CreateDir("repo");
        await using var orch = Build(new ScriptedPtyProcess());
        var agent = ClaudeAgent(orch, "unknown", WorktreeInfo.Borrowed(cwd), WorkLocation.BorrowedCwd);
        Stage(cwd, Foreign);

        await orch.DetectSessionIdForTest(agent, DateTime.UtcNow.AddMinutes(-1));

        await Assert.That(orch.DiscoveryStartsForTest).IsEqualTo(0);
        await Assert.That(agent.TranscriptPath).IsNull();
        await Assert.That(agent.SessionId).IsNull();
    }

    [Test]
    public async Task An_owned_worktree_agent_with_no_session_id_is_still_linked_by_cwd() {
        var worktree = Tmp.CreateDir("wt");
        await using var orch = Build(new ScriptedPtyProcess());
        var agent = ClaudeAgent(orch, "owned", new WorktreeInfo(worktree, "capacitor/wt", Tmp.CreateDir("source")), WorkLocation.OwnedWorktree);
        var own = Stage(worktree, Own);

        _ = orch.DetectSessionIdForTest(agent, DateTime.UtcNow.AddMinutes(-1));
        await WaitHarness.PollUntilAsync(() => agent.TranscriptPath is not null);

        await Assert.That(agent.TranscriptPath).IsEqualTo(own);
        await Assert.That(agent.SessionId).IsEqualTo(SessionIds.Canonical(Own));
        await agent.ExitedCts.CancelAsync();
    }

    static AgentInstance ClaudeAgent(AgentOrchestrator orch, string id, WorktreeInfo worktree, WorkLocation work) {
        var agent = new AgentInstance(id, null, "", null, worktree.Path, "claude",
            new PtyHostedAgentRuntime("claude", new ScriptedPtyProcess(), TimeProvider.System), worktree, new CancellationTokenSource()) {
            ActivityClock = new AgentActivityClock(TimeProvider.System),
            CreatedAt     = DateTime.UtcNow,
            LastOutputAt  = DateTime.UtcNow,
            IsPrivate     = true,
            Work          = work
        };
        orch.RegisterAgentForTest(agent);

        return agent;
    }

    static ResumeLedgerEntry InPlaceEntry(string agentId, string cwd, string session) {
        var stamp  = TimeProvider.System.GetUtcNow();
        var launch = new ResumeLaunchDescriptor(
            Prompt: "build the thing", Model: "opus", Effort: null, RepoPath: cwd,
            WorktreePath: cwd, Work: WorkLocation.BorrowedCwd,
            GitRelativeCwd: null, SandboxPolicy: null, ApprovalPolicy: null, PermissionPreset: null,
            McpConfigPath: null, Title: "the thing", IsPrivate: false, Origin: "server",
            RequesterUserId: "user-1", RequesterDisplay: "User One", ConsentSource: "default",
            ConsentAt: stamp.ToString("O"), FlowRunId: null, FlowRole: null,
            WorktreeBranch: null, WorktreeIsStandalone: false, WorktreeSourceRepo: cwd);

        return new ResumeLedgerEntry(
            ResumeLedgerEntry.CurrentSchemaVersion, agentId, "daemon-1", stamp, stamp, "claude", "pty",
            SessionIds.Canonical(session), session, launch, ResumeState.Resuming, Attempts: 0, LastError: null,
            PredecessorInstanceId: "instance-old", ResumingAt: stamp);
    }
}
