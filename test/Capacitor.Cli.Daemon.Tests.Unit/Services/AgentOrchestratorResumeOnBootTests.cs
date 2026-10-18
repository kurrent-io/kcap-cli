using System.Collections.Concurrent;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Resume;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// A booting daemon reads its predecessor's resume ledger, decides which entries to bring back, and
/// relaunches each one under its own agent id with the vendor session and a nudge prompt.
/// </summary>
public class AgentOrchestratorResumeOnBootTests {
    [TempDir] public required TempDir Tmp { get; init; }

    sealed class ResumableFactory : IHostedAgentRuntimeFactory {
        int _inFlight;
        int _maxInFlight;

        public string CliPath => "unused";
        public string Vendor { get; init; } = "cursor";
        public bool SupportsUnattended => false;
        public bool IsAvailable() => true;
        public bool ThrowsFromSupports { get; init; }
        public bool SupportsRestartResume(string? runtimeTransport) =>
            ThrowsFromSupports ? throw new InvalidOperationException("the factory faulted") : true;

        public ConcurrentQueue<RuntimeStartContext> Contexts { get; } = new();
        public TaskCompletionSource? Gate { get; set; }
        public bool ExitBeforeHandshake { get; init; }
        public bool PtyStyle { get; init; }
        public int? ExitImmediatelyWith { get; init; }
        public int InFlight => Volatile.Read(ref _inFlight);
        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        public async Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
            Contexts.Enqueue(ctx);
            var now = Interlocked.Increment(ref _inFlight);
            InterlockedMax(ref _maxInFlight, now);

            try {
                if (Gate is { } gate) await gate.Task.WaitAsync(ct);
                if (ExitBeforeHandshake) throw new InvalidOperationException("the agent exited before its handshake");

                if (ExitImmediatelyWith is { } code) {
                    var exited = new FakeHostedAgentRuntime(Vendor, emitsTerminalOutput: true) { ExitCode = code };
                    exited.ExitGate.SetResult();

                    return new HostedRuntimeStart(exited, McpConfigPath: null, Transcript: null);
                }

                if (PtyStyle)
                    return new HostedRuntimeStart(new FakeHostedAgentRuntime(Vendor, emitsTerminalOutput: true), McpConfigPath: null, Transcript: null);

                var runtime = new FakeAcpRuntime { Vendor = Vendor, Cwd = ctx.Worktree.Path };

                return new HostedRuntimeStart(runtime, McpConfigPath: null, Transcript: runtime);
            } finally {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        static void InterlockedMax(ref int target, int value) {
            int seen;
            while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
        }
    }

    sealed record Fixture(AgentOrchestrator Orch, CaptureServerConnection Server, ResumableFactory Factory, DaemonConfig Config, string Repo);

    Fixture Build(ResumableFactory? factory = null, Action<DaemonConfig>? configure = null, TimeProvider? time = null) {
        var repo   = Tmp.CreateDir("repo");
        var server = new CaptureServerConnection();
        factory ??= new ResumableFactory();
        DaemonConfig? captured = null;
        var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            allowedRepoPath: repo, extraRuntimeFactories: [factory],
            configure: c => {
                captured = c;
                c.InstanceId = "instance-now";
                c.ResumeReadinessWindow = TimeSpan.FromMilliseconds(100);
                configure?.Invoke(c);
            },
            timeProvider: time);

        return new Fixture(orch, server, factory, captured!, repo);
    }

    ResumeLedgerEntry Entry(
            Fixture f, string agentId, string? worktree = null, string? vendorSession = "vendor-session-1",
            bool isPrivate = false, ResumeState state = ResumeState.Resuming, DateTimeOffset? at = null,
            string? predecessor = "instance-old", string? daemonInstanceId = null, bool standalone = false,
            string vendor = "cursor", string? permissionMode = null) {
        var stamp = at ?? TimeProvider.System.GetUtcNow();
        var launch = new ResumeLaunchDescriptor(
            Prompt: "build the thing", Model: "auto", Effort: null, RepoPath: f.Repo,
            WorktreePath: worktree ?? Tmp.CreateDir($"wt-{agentId}"), Work: WorkLocation.OwnedWorktree,
            GitRelativeCwd: null, SandboxPolicy: null, ApprovalPolicy: null, PermissionPreset: null,
            McpConfigPath: null, Title: "the thing", IsPrivate: isPrivate, Origin: "server",
            RequesterUserId: "user-1", RequesterDisplay: "User One", ConsentSource: "default",
            ConsentAt: stamp.ToString("O"), FlowRunId: null, FlowRole: null,
            WorktreeBranch: $"capacitor/custom-{agentId}", WorktreeIsStandalone: standalone, WorktreeSourceRepo: f.Repo,
            PermissionMode: permissionMode);
        var entry = new ResumeLedgerEntry(
            ResumeLedgerEntry.CurrentSchemaVersion, agentId, "daemon-1", stamp, stamp, vendor, "acp",
            $"session-{agentId}", vendorSession, launch, state, Attempts: 0, LastError: null,
            PredecessorInstanceId: predecessor, ResumingAt: state == ResumeState.Resuming ? stamp : null,
            DaemonInstanceId: daemonInstanceId);
        f.Orch.ResumeLedger!.Write(entry);

        return entry;
    }

    [Test]
    public async Task A_resuming_entry_relaunches_under_its_agent_id_with_the_vendor_session_and_nudge() {
        var f = Build();
        await using var _ = f.Orch;
        var entry = Entry(f, "resume-1");
        await File.WriteAllBytesAsync(f.Orch.ResumeLedger!.RingPath("resume-1"), "earlier output"u8.ToArray());

        f.Orch.LoadResumeLedger();
        await f.Orch.ResumeOnBootAsync(CancellationToken.None);

        var ctx = f.Factory.Contexts.Single();
        await Assert.That(ctx.AgentId).IsEqualTo("resume-1");
        await Assert.That(ctx.ResumeSessionId).IsEqualTo("vendor-session-1");
        await Assert.That(ctx.RequestedTransport).IsEqualTo(entry.Transport);
        await Assert.That(ctx.Prompt).IsEqualTo(ResumeNudge.Build(entry.ResumingAt!.Value, "build the thing"));
        await Assert.That(ctx.Worktree.Path).IsEqualTo(entry.Launch.WorktreePath);
        await Assert.That(ctx.Worktree.Branch).IsEqualTo("capacitor/custom-resume-1");
        await Assert.That(ctx.Worktree.SourceRepo).IsEqualTo(f.Repo);
        await Assert.That(ctx.Worktree.IsStandalone).IsFalse();

        var agent = f.Orch.GetAgentForTest("resume-1");
        await Assert.That(agent).IsNotNull();
        await Assert.That(f.Server.LaunchFailedCalls).IsEmpty();

        var after = f.Orch.ResumeLedger!.TryRead("resume-1")!;
        await Assert.That(after.State).IsEqualTo(ResumeState.Running);
        await Assert.That(after.Launch.Prompt).IsEqualTo("build the thing");
        await Assert.That(after.DaemonInstanceId).IsEqualTo("instance-now");
        await Assert.That(agent!.ResumePending).IsFalse();

        var buffered = System.Text.Encoding.UTF8.GetString(agent!.OutputBuffer.Snapshot());
        await Assert.That(buffered).StartsWith("earlier output");
        await Assert.That(File.Exists(f.Orch.ResumeLedger!.RingPath("resume-1"))).IsFalse();

        var consentLog = await File.ReadAllTextAsync(
            Path.Combine(f.Config.Store.StateDirectory(f.Config.Name), "consent-decisions.jsonl"));
        await Assert.That(consentLog).Contains("\"agent_id\":\"resume-1\"");
        await Assert.That(consentLog).Contains("\"source\":\"resume\"");
        await Assert.That(f.Orch.ResumingClaims()).IsEmpty();
    }

    /// <summary>A stop that missed the live table while the resume published finds the agent inside the
    /// discard's lock: nothing is discarded, and the agent comes back to be stopped as live.</summary>
    [Test]
    public async Task A_discard_of_a_published_resume_hands_back_the_live_agent() {
        var f = Build();
        await using var _ = f.Orch;
        Entry(f, "resume-live");

        f.Orch.LoadResumeLedger();
        await f.Orch.ResumeOnBootAsync(CancellationToken.None);

        var discarded = f.Orch.TryDiscardResumeEntry("resume-live", out var live);

        await Assert.That(discarded).IsFalse();
        await Assert.That(live).IsSameReferenceAs(f.Orch.GetAgentForTest("resume-live"));
        await Assert.That(f.Orch.ResumeLedger!.TryRead("resume-live")).IsNotNull();
    }

    [Test]
    public async Task A_resumed_claude_agent_keeps_its_permission_mode() {
        var f = Build(new ResumableFactory { Vendor = "claude", PtyStyle = true });
        await using var _ = f.Orch;
        Entry(f, "resume-plan", vendor: "claude", permissionMode: "plan");

        f.Orch.LoadResumeLedger();
        await f.Orch.ResumeOnBootAsync(CancellationToken.None);

        await Assert.That(f.Factory.Contexts.Single().PermissionMode).IsEqualTo("plan");
    }

    [Test]
    public async Task A_resumed_pty_agent_publishes_the_ledgers_session_ids_before_any_discovery() {
        var f = Build(new ResumableFactory { PtyStyle = true });
        await using var _ = f.Orch;
        Entry(f, "resume-pty");

        f.Orch.LoadResumeLedger();
        await f.Orch.ResumeOnBootAsync(CancellationToken.None);

        var agent = f.Orch.GetAgentForTest("resume-pty")!;
        await Assert.That(agent.SessionId).IsEqualTo("session-resume-pty");
        await Assert.That(agent.VendorSession).IsEqualTo("vendor-session-1");
    }

    [Test]
    public async Task Claims_list_non_private_candidates_only() {
        var f = Build();
        await using var _ = f.Orch;
        Entry(f, "shared-1");
        Entry(f, "private-1", isPrivate: true);

        f.Orch.LoadResumeLedger();

        var claims = f.Orch.ResumingClaims();
        await Assert.That(claims.Length).IsEqualTo(1);
        await Assert.That(claims[0]).IsEqualTo(new ResumingAgentClaim("shared-1", "instance-old", "session-shared-1"));
        await Assert.That(f.Orch.BuildStatusReport().ResumingAgents).IsEquivalentTo(claims);
    }

    [Test]
    public async Task Entries_older_than_the_age_cap_become_interrupted_too_old() {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
        var f = Build(time: time);
        await using var _ = f.Orch;
        Entry(f, "old-1", state: ResumeState.Running, at: time.GetUtcNow() - TimeSpan.FromDays(2));
        f.Config.Store.EnsureDirectory();
        var heartbeat = f.Config.Store.HeartbeatPath(f.Config.Name);
        await File.WriteAllBytesAsync(heartbeat, []);
        File.SetLastWriteTimeUtc(heartbeat, (time.GetUtcNow() - TimeSpan.FromDays(2)).UtcDateTime);

        f.Orch.LoadResumeLedger();
        await f.Orch.ResumeOnBootAsync(CancellationToken.None);

        var entry = f.Orch.ResumeLedger!.TryRead("old-1")!;
        await Assert.That(entry.State).IsEqualTo(ResumeState.Interrupted);
        await Assert.That(entry.LastError).IsEqualTo("too_old");
        await Assert.That(f.Factory.Contexts).IsEmpty();
        await Assert.That(f.Orch.ResumingClaims()).IsEmpty();
    }

    [Test]
    public async Task Auto_resume_off_interrupts_everything_and_launches_nothing() {
        var f = Build(configure: c => c.AutoResume = false);
        await using var _ = f.Orch;
        Entry(f, "off-1");
        Entry(f, "off-2", state: ResumeState.Running);

        f.Orch.LoadResumeLedger();
        await f.Orch.ResumeOnBootAsync(CancellationToken.None);

        foreach (var id in new[] { "off-1", "off-2" }) {
            var entry = f.Orch.ResumeLedger!.TryRead(id)!;
            await Assert.That(entry.State).IsEqualTo(ResumeState.Interrupted);
            await Assert.That(entry.LastError).IsEqualTo("auto_resume_off");
            await Assert.That(f.Server.ShutdownReports.Select(r => (r.AgentId, r.Status, r.Reason)))
                .Contains((id, "Failed", "resume_failed:auto_resume_off"));
        }

        await Assert.That(f.Factory.Contexts).IsEmpty();
        await Assert.That(f.Orch.ResumingClaims()).IsEmpty();
    }

    [Test]
    public async Task A_missing_worktree_interrupts_with_worktree_missing() {
        var f = Build();
        await using var _ = f.Orch;
        Entry(f, "gone-1", worktree: Tmp.PathTo("never-created"));

        f.Orch.LoadResumeLedger();
        await f.Orch.ResumeOnBootAsync(CancellationToken.None);

        var entry = f.Orch.ResumeLedger!.TryRead("gone-1")!;
        await Assert.That(entry.State).IsEqualTo(ResumeState.Interrupted);
        await Assert.That(entry.LastError).IsEqualTo("worktree_missing");
        await Assert.That(f.Factory.Contexts).IsEmpty();
    }

    [Test]
    public async Task An_unresolved_predecessor_process_interrupts_with_predecessor_process_unresolved() {
        var f = Build();
        await using var _ = f.Orch;
        Entry(f, "ambiguous-1");
        f.Orch.WritePidRecordForTest(new AgentPidRecord(
            "ambiguous-1", 999_999, "", PidIdentityKind.IdentityUnavailable, "Default", "cursor", null, null,
            f.Orch.DaemonIdForTest, "previous-epoch", TimeProvider.System.GetUtcNow()));

        f.Orch.LoadResumeLedger();

        var entry = f.Orch.ResumeLedger!.TryRead("ambiguous-1")!;
        await Assert.That(entry.State).IsEqualTo(ResumeState.Interrupted);
        await Assert.That(entry.LastError).IsEqualTo("predecessor_process_unresolved");
        await Assert.That(f.Orch.ResumingClaims()).IsEmpty();
    }

    /// <summary>The reaper could not prove the old process gone, so an operator's retry would run a second
    /// process on the session.</summary>
    [Test]
    public async Task A_retry_is_refused_while_the_predecessor_process_is_unresolved() {
        var f = Build();
        await using var _ = f.Orch;
        Entry(f, "ambiguous-2", state: ResumeState.Interrupted);
        f.Orch.WritePidRecordForTest(new AgentPidRecord(
            "ambiguous-2", 999_999, "", PidIdentityKind.IdentityUnavailable, "Default", "cursor", null, null,
            f.Orch.DaemonIdForTest, "previous-epoch", TimeProvider.System.GetUtcNow()));

        var ack = await f.Orch.ResumeInterruptedAsync("ambiguous-2", CancellationToken.None);

        await Assert.That(ack).IsEqualTo("blocked:predecessor_process_unresolved");
        await Assert.That(f.Orch.ResumeLedger!.TryRead("ambiguous-2")!.State).IsEqualTo(ResumeState.Interrupted);
        await Assert.That(f.Orch.ResumingClaims()).IsEmpty();
        await Assert.That(f.Factory.Contexts).IsEmpty();
    }

    /// <summary>A newer daemon wrote the entry; this one cannot know what it means and must not rewrite it.</summary>
    [Test]
    public async Task A_retry_is_refused_for_an_entry_written_by_a_newer_daemon() {
        var f = Build();
        await using var _ = f.Orch;
        var entry = Entry(f, "newer-1", state: ResumeState.Interrupted);
        f.Orch.ResumeLedger!.Write(entry with { SchemaVersion = ResumeLedgerEntry.CurrentSchemaVersion + 1 });

        var ack = await f.Orch.ResumeInterruptedAsync("newer-1", CancellationToken.None);

        await Assert.That(ack).IsEqualTo("blocked:ledger_newer_than_daemon");
        var json = await File.ReadAllTextAsync(f.Orch.ResumeLedger!.EntryPath("newer-1"));
        await Assert.That(json).Contains($"\"schema_version\":{ResumeLedgerEntry.CurrentSchemaVersion + 1}");
        await Assert.That(json).Contains("\"state\":\"interrupted\"");
        await Assert.That(f.Factory.Contexts).IsEmpty();
    }

    [Test]
    public async Task The_lane_runs_at_most_ResumeConcurrency_launches_at_once() {
        var factory = new ResumableFactory { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var f = Build(factory, c => c.ResumeConcurrency = 2);
        await using var _ = f.Orch;
        for (var i = 1; i <= 4; i++) Entry(f, $"lane-{i}");

        f.Orch.LoadResumeLedger();
        var lane = f.Orch.ResumeOnBootAsync(CancellationToken.None);

        await WaitHarness.PollUntilAsync(() => factory.InFlight == 2);
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        await Assert.That(factory.InFlight).IsEqualTo(2);
        await Assert.That(factory.Contexts.Count).IsEqualTo(2);

        factory.Gate.SetResult();
        await WaitHarness.WaitBoundedAsync(lane, "the resume lane");

        await Assert.That(factory.Contexts.Count).IsEqualTo(4);
        await Assert.That(factory.MaxInFlight).IsEqualTo(2);
    }

    /// <summary>Concurrent resumes reach the capacity check before either publishes, so the check has
    /// to reserve the slot it grants.</summary>
    [Test]
    public async Task Concurrent_resumes_against_the_last_slot_launch_one_and_interrupt_the_other_for_capacity() {
        var factory = new ResumableFactory { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var f = Build(factory, c => {
            c.ResumeConcurrency   = 2;
            c.MaxConcurrentAgents = 1;
        });
        await using var _ = f.Orch;
        f.Orch.ContinueEntry = (_, _, _) => Task.FromResult(ResumeOutcome.Interrupted);
        Entry(f, "slot-1");
        Entry(f, "slot-2");

        f.Orch.LoadResumeLedger();
        var lane = f.Orch.ResumeOnBootAsync(CancellationToken.None);

        await WaitHarness.PollUntilAsync(() => factory.InFlight >= 1);
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        await Assert.That(factory.InFlight).IsEqualTo(1);

        factory.Gate.SetResult();
        await WaitHarness.WaitBoundedAsync(lane, "the resume lane");

        var launched = factory.Contexts.Single().AgentId;
        var refused  = launched == "slot-1" ? "slot-2" : "slot-1";
        await Assert.That(f.Orch.GetAgentForTest(launched)).IsNotNull();
        await Assert.That(f.Orch.GetAgentForTest(refused)).IsNull();

        var after = f.Orch.ResumeLedger!.TryRead(refused)!;
        await Assert.That(after.State).IsEqualTo(ResumeState.Interrupted);
        await Assert.That(after.LastError).IsEqualTo("daemon_capacity");
    }

    /// <summary>A web-UI stop names an agent this daemon is not running yet; it withdraws the candidate so
    /// the lane never launches it.</summary>
    [Test]
    public async Task A_server_stop_of_a_waiting_candidate_withdraws_it_from_the_lane() {
        var factory = new ResumableFactory { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var f = Build(factory, c => c.ResumeConcurrency = 1);
        await using var _ = f.Orch;
        Entry(f, "wait-1");
        Entry(f, "wait-2");

        f.Orch.LoadResumeLedger();
        var lane = f.Orch.ResumeOnBootAsync(CancellationToken.None);
        await WaitHarness.PollUntilAsync(() => factory.InFlight == 1);
        var running = factory.Contexts.Single().AgentId;
        var waiting = running == "wait-1" ? "wait-2" : "wait-1";

        await f.Orch.HandleServerStopAgentForTest(waiting);
        factory.Gate.SetResult();
        await WaitHarness.WaitBoundedAsync(lane, "the resume lane");

        await Assert.That(factory.Contexts.Select(c => c.AgentId)).IsEquivalentTo([running]);
        await Assert.That(f.Orch.ResumeLedger!.TryRead(waiting)).IsNull();
        await Assert.That(f.Orch.ResumingClaims().Any(c => c.AgentId == waiting)).IsFalse();
    }

    /// <summary>A stop while the runtime is still handshaking finds no published agent to mark; the
    /// attempt itself has to give up, or it would publish the agent and rewrite its entry afterwards.</summary>
    [Test]
    public async Task A_stop_while_the_runtime_handshakes_publishes_nothing_and_writes_no_entry() {
        var factory = new ResumableFactory { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var f = Build(factory);
        await using var _ = f.Orch;
        Entry(f, "handshake-1");
        var continued = 0;
        f.Orch.ContinueEntry = (_, _, _) => {
            Interlocked.Increment(ref continued);
            return Task.FromResult(ResumeOutcome.Interrupted);
        };

        f.Orch.LoadResumeLedger();
        var lane = f.Orch.ResumeOnBootAsync(CancellationToken.None);
        await WaitHarness.PollUntilAsync(() => factory.InFlight == 1);

        await f.Orch.HandleStopAgent("handshake-1");
        factory.Gate.SetResult();
        await WaitHarness.WaitBoundedAsync(lane, "the resume lane");

        await Assert.That(f.Orch.GetAgentForTest("handshake-1")).IsNull();
        await Assert.That(f.Orch.ResumeLedger!.TryRead("handshake-1")).IsNull();
        await Assert.That(continued).IsEqualTo(0);
        await Assert.That(f.Server.ShutdownReports.Any(r => r.Reason.StartsWith("resume_failed", StringComparison.Ordinal))).IsFalse();
        await Assert.That(f.Orch.LedgerOnlyAgents()).IsEmpty();
    }

    /// <summary>An end-agents shutdown ends a resumed agent still inside its readiness window and a
    /// candidate still waiting for the lane, so neither comes back on the next start.</summary>
    [Test]
    public async Task An_end_agents_shutdown_during_recovery_leaves_no_ledger_entries() {
        var f = Build(configure: c => {
            c.ResumeConcurrency     = 1;
            c.ResumeReadinessWindow = TimeSpan.FromSeconds(30);
        });
        Entry(f, "ending-1");
        Entry(f, "ending-2");

        f.Orch.LoadResumeLedger();
        var lane = f.Orch.ResumeOnBootAsync(CancellationToken.None);
        await WaitHarness.PollUntilAsync(() => f.Orch.GetAgentForTest("ending-1") is not null || f.Orch.GetAgentForTest("ending-2") is not null);

        f.Orch.ShutdownIntent = ShutdownIntent.EndAgents;
        await f.Orch.DisposeAsync();
        await WaitHarness.WaitBoundedAsync(lane, "the resume lane");

        await Assert.That(f.Orch.ResumeLedger!.ReadAll()).IsEmpty();
        await Assert.That(f.Factory.Contexts.Count).IsEqualTo(1);
    }

    [Test]
    public async Task A_start_that_throws_tries_continue_once_then_interrupts_and_reports_resume_failed() {
        var f = Build(new ResumableFactory { ExitBeforeHandshake = true });
        await using var _ = f.Orch;
        var entry = Entry(f, "fails-1");
        var continued = new ConcurrentQueue<(string AgentId, string Reason, int Attempts)>();
        f.Orch.ContinueEntry = (e, reason, _) => {
            continued.Enqueue((e.AgentId, reason, e.Attempts));
            return Task.FromResult(ResumeOutcome.Interrupted);
        };

        f.Orch.LoadResumeLedger();
        await f.Orch.ResumeOnBootAsync(CancellationToken.None);

        await Assert.That(continued.Single()).IsEqualTo(("fails-1", "runtime_exited", 1));

        var after = f.Orch.ResumeLedger!.TryRead("fails-1")!;
        await Assert.That(after.State).IsEqualTo(ResumeState.Interrupted);
        await Assert.That(after.LastError).IsEqualTo("runtime_exited");
        await Assert.That(after.Attempts).IsEqualTo(1);
        await Assert.That(Directory.Exists(entry.Launch.WorktreePath)).IsTrue();

        await Assert.That(f.Server.ShutdownReports.Select(r => (r.AgentId, r.Status, r.Reason)))
            .Contains(("fails-1", "Failed", "resume_failed:runtime_exited"));
        await Assert.That(f.Orch.ResumingClaims()).IsEmpty();
    }

    /// <summary>A fault outside the resume's own failure path leaves the entry where <c>kcap agent ls</c>
    /// shows it and <c>kcap agent resume</c> can retry it.</summary>
    [Test]
    public async Task An_unexpected_fault_in_the_lane_interrupts_the_entry_with_the_exception_type() {
        var f = Build(new ResumableFactory { ThrowsFromSupports = true });
        await using var _ = f.Orch;
        Entry(f, "faults-1");

        f.Orch.LoadResumeLedger();
        await f.Orch.ResumeOnBootAsync(CancellationToken.None);

        var after = f.Orch.ResumeLedger!.TryRead("faults-1")!;
        await Assert.That(after.State).IsEqualTo(ResumeState.Interrupted);
        await Assert.That(after.LastError).IsEqualTo(nameof(InvalidOperationException));
        await Assert.That(f.Orch.LedgerOnlyAgents().Single().State).IsEqualTo($"interrupted ({nameof(InvalidOperationException)})");
        await Assert.That(await f.Orch.ResumeInterruptedAsync("faults-1", CancellationToken.None)).IsEqualTo("resuming");
    }

    /// <summary>The worktree is a standalone copy, so a cleanup that treated the resumed agent as
    /// ended would delete it outright.</summary>
    [Test]
    public async Task A_runtime_that_exits_before_handshake_tries_continue_once_then_interrupts_and_reports_resume_failed() {
        var f = Build(new ResumableFactory { ExitImmediatelyWith = 3 });
        await using var _ = f.Orch;
        var worktree = Tmp.CreateDir("wt-standalone");
        Directory.CreateDirectory(Path.Combine(worktree, ".git"));
        var entry = Entry(f, "exits-1", worktree: worktree, standalone: true);
        var continued = new ConcurrentQueue<(string AgentId, string Reason, int Attempts)>();
        f.Orch.ContinueEntry = (e, reason, _) => {
            continued.Enqueue((e.AgentId, reason, e.Attempts));
            return Task.FromResult(ResumeOutcome.Interrupted);
        };

        f.Orch.LoadResumeLedger();
        await f.Orch.ResumeOnBootAsync(CancellationToken.None);
        await WaitHarness.PollUntilAsync(() => f.Orch.GetAgentForTest("exits-1") is null);

        await Assert.That(continued.Single()).IsEqualTo(("exits-1", "runtime_exited", 1));
        await Assert.That(Directory.Exists(entry.Launch.WorktreePath)).IsTrue();

        var after = f.Orch.ResumeLedger!.TryRead("exits-1")!;
        await Assert.That(after.State).IsEqualTo(ResumeState.Interrupted);
        await Assert.That(after.LastError).IsEqualTo("runtime_exited (exit code 3)");
        await Assert.That(after.Attempts).IsEqualTo(1);

        await Assert.That(f.Server.ShutdownReports.Select(r => (r.AgentId, r.Status, r.Reason)))
            .Contains(("exits-1", "Failed", "resume_failed:runtime_exited"));
        await Assert.That(f.Server.ShutdownReportExitCodes).Contains(("exits-1", (int?)3));
    }

    [Test]
    public async Task A_stop_inside_the_readiness_window_ends_the_agent_without_a_failed_resume() {
        var f = Build(configure: c => c.ResumeReadinessWindow = TimeSpan.FromSeconds(20));
        await using var _ = f.Orch;
        var worktree = Tmp.CreateDir("wt-stopped");
        Directory.CreateDirectory(Path.Combine(worktree, ".git"));
        Entry(f, "stopped-1", worktree: worktree, standalone: true);
        var continued = 0;
        f.Orch.ContinueEntry = (_, _, _) => {
            Interlocked.Increment(ref continued);
            return Task.FromResult(ResumeOutcome.Interrupted);
        };

        f.Orch.LoadResumeLedger();
        var lane = f.Orch.ResumeOnBootAsync(CancellationToken.None);
        await WaitHarness.PollUntilAsync(() => f.Orch.GetAgentForTest("stopped-1") is not null);

        await f.Orch.HandleStopAgent("stopped-1");
        await WaitHarness.WaitBoundedAsync(lane, "the resume lane");
        await WaitHarness.PollUntilAsync(() => f.Orch.GetAgentForTest("stopped-1") is null);

        await Assert.That(continued).IsEqualTo(0);
        await Assert.That(f.Server.ShutdownReports.Any(r => r.Reason.StartsWith("resume_failed", StringComparison.Ordinal))).IsFalse();
        await Assert.That(Directory.Exists(worktree)).IsFalse();
        await Assert.That(f.Orch.ResumeLedger!.TryRead("stopped-1")).IsNull();
    }

    [Test]
    public async Task A_crash_left_entry_claims_with_the_instance_that_wrote_it() {
        var f = Build();
        await using var _ = f.Orch;
        Entry(f, "crashed-1", state: ResumeState.Running, predecessor: null, daemonInstanceId: "instance-crashed");

        f.Orch.LoadResumeLedger();

        await Assert.That(f.Orch.ResumingClaims().Single().PredecessorInstanceId).IsEqualTo("instance-crashed");
    }

    [Test]
    public async Task A_failed_private_resume_sends_nothing_for_its_agent() {
        var f = Build(new ResumableFactory { ExitBeforeHandshake = true });
        await using var _ = f.Orch;
        Entry(f, "private-fails-1", isPrivate: true);

        f.Orch.LoadResumeLedger();
        await f.Orch.ResumeOnBootAsync(CancellationToken.None);

        // The native resume, then the continue under a new agent id; both fail.
        await Assert.That(f.Factory.Contexts.Select(c => c.AgentId).Distinct().Count()).IsEqualTo(2);
        await Assert.That(f.Server.LaunchFailedCalls).IsEmpty();
        await Assert.That(f.Server.ShutdownReports).IsEmpty();
        await Assert.That(f.Orch.ResumeLedger!.TryRead("private-fails-1")!.State).IsEqualTo(ResumeState.Interrupted);
    }

    [Test]
    public async Task Worktree_keep_set_lists_candidate_worktrees() {
        var f = Build();
        await using var _ = f.Orch;
        var candidate   = Entry(f, "keep-1");
        var interrupted = Entry(f, "keep-2", state: ResumeState.Interrupted);

        f.Orch.LoadResumeLedger();

        var keep = f.Orch.LedgerWorktreePaths();
        await Assert.That(keep).Contains(candidate.Launch.WorktreePath);
        await Assert.That(keep).Contains(interrupted.Launch.WorktreePath);
    }

    /// <summary>The boot sweep runs before any ledgered agent publishes, so the ledger alone has to keep
    /// the attachment directories a resumed vendor session still refers to.</summary>
    [Test]
    public async Task The_attachment_sweep_keeps_ledgered_agents_and_removes_unknown_ones() {
        var f = Build();
        await using var _ = f.Orch;
        Entry(f, "attach-1");
        Entry(f, "attach-2", state: ResumeState.Interrupted);
        var candidate   = Directory.CreateDirectory(f.Orch.AttachmentStore.DirectoryFor("attach-1")).FullName;
        var interrupted = Directory.CreateDirectory(f.Orch.AttachmentStore.DirectoryFor("attach-2")).FullName;
        var unknown     = Directory.CreateDirectory(f.Orch.AttachmentStore.DirectoryFor("unknown-1")).FullName;

        f.Orch.LoadResumeLedger();
        f.Orch.AttachmentStore.SweepOrphans(f.Orch.IsLiveAttachmentStem, NullLogger.Instance);

        await Assert.That(Directory.Exists(candidate)).IsTrue();
        await Assert.That(Directory.Exists(interrupted)).IsTrue();
        await Assert.That(Directory.Exists(unknown)).IsFalse();
    }

    [Test]
    public async Task Interrupted_entries_older_than_thirty_days_are_reaped_with_their_ring() {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero));
        var f = Build(time: time);
        await using var _ = f.Orch;
        var stale = Entry(f, "stale-1", state: ResumeState.Interrupted, at: time.GetUtcNow() - TimeSpan.FromDays(31));
        var young = Entry(f, "young-1", state: ResumeState.Interrupted, at: time.GetUtcNow() - TimeSpan.FromDays(29));
        await File.WriteAllBytesAsync(f.Orch.ResumeLedger!.RingPath("stale-1"), "old output"u8.ToArray());

        f.Orch.LoadResumeLedger();

        await Assert.That(f.Orch.ResumeLedger!.TryRead("stale-1")).IsNull();
        await Assert.That(File.Exists(f.Orch.ResumeLedger!.RingPath("stale-1"))).IsFalse();
        await Assert.That(f.Orch.ResumeLedger!.TryRead("young-1")!.State).IsEqualTo(ResumeState.Interrupted);
        await Assert.That(f.Orch.LedgerWorktreePaths()).DoesNotContain(stale.Launch.WorktreePath);
        await Assert.That(f.Orch.LedgerWorktreePaths()).Contains(young.Launch.WorktreePath);
    }
}
