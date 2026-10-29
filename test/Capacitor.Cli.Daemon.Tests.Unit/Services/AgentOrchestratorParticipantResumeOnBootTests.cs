using System.Collections.Concurrent;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Resume;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>A booting daemon resumes a flow participant only when the boot just before it stopped that
/// participant for resume; every other participant entry is reported failed, so its flow heals as it
/// does without a resume.</summary>
public class AgentOrchestratorParticipantResumeOnBootTests {
    static readonly DateTimeOffset Now = new(2026, 10, 23, 10, 0, 0, TimeSpan.Zero);

    [TempDir] public required TempDir Tmp { get; init; }

    /// The harness deletes its own daemon store on dispose, so what a shutdown leaves is read from this one.
    [TempDaemonPaths] public required TempDaemonStore Store { get; init; }

    sealed class ReviewerFactory : IHostedAgentRuntimeFactory {
        public string CliPath => "unused";
        public string Vendor => "cursor";
        public bool SupportsUnattended => true;
        public bool IsAvailable() => true;
        public bool SupportsReviewerRestartResume(string? runtimeTransport, WorkLocation work) => true;
        public ConcurrentQueue<RuntimeStartContext> Contexts { get; } = new();
        public bool Fails { get; init; }
        public TaskCompletionSource? Gate { get; init; }
        public FakeHostedAgentRuntime? LastRuntime { get; private set; }

        public async Task<HostedRuntimeStart> StartAsync(RuntimeStartContext ctx, CancellationToken ct) {
            Contexts.Enqueue(ctx);
            if (Gate is { } gate) await gate.Task.WaitAsync(ct);
            if (Fails) throw new InvalidOperationException("the reviewer could not reopen its session");

            LastRuntime = new FakeHostedAgentRuntime(Vendor, emitsTerminalOutput: false);

            return new HostedRuntimeStart(LastRuntime, McpConfigPath: null, Transcript: null);
        }
    }

    sealed record Fixture(AgentOrchestrator Orch, CaptureServerConnection Server, ReviewerFactory Factory, string Repo);

    Fixture Build(ReviewerFactory? factory = null, Action<DaemonConfig>? configure = null) {
        var repo   = Tmp.CreateDir("repo");
        var server = new CaptureServerConnection();
        factory ??= new ReviewerFactory();
        var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            allowedRepoPath: repo, extraRuntimeFactories: [factory],
            configure: c => {
                c.Store                 = Store.Store;
                c.InstanceId            = "instance-now";
                c.PriorInstanceId       = "instance-old";
                c.ResumeReadinessWindow = TimeSpan.FromMilliseconds(100);
                configure?.Invoke(c);
            });

        return new Fixture(orch, server, factory, repo);
    }

    ResumeLedgerEntry Participant(
            Fixture f, string agentId, ResumeState state = ResumeState.Resuming, DateTimeOffset? resumingAt = null,
            string? predecessor = "instance-old", string? vendorSession = "vendor-session-1", string? accountId = null,
            string? worktree = null) {
        var stamp = resumingAt ?? TimeProvider.System.GetUtcNow();
        var launch = new ResumeLaunchDescriptor(
            Prompt: "review this", Model: "default", Effort: null, RepoPath: f.Repo,
            WorktreePath: worktree ?? Tmp.CreateDir($"wt-{agentId}"), Work: WorkLocation.OwnedWorktree,
            GitRelativeCwd: null, SandboxPolicy: null, ApprovalPolicy: null, PermissionPreset: null,
            McpConfigPath: null, Title: "review", IsPrivate: false, Origin: "server",
            RequesterUserId: "user-1", RequesterDisplay: "User One", ConsentSource: "default",
            ConsentAt: stamp.ToString("O"), FlowRunId: "flow-1", FlowRole: "reviewer",
            WorktreeBranch: $"capacitor/{agentId}", WorktreeIsStandalone: true, WorktreeSourceRepo: f.Repo,
            AccountId: accountId);
        var entry = new ResumeLedgerEntry(
            ResumeLedgerEntry.CurrentSchemaVersion, agentId, "daemon-1", stamp, stamp, "cursor", "pty",
            $"session-{agentId}", vendorSession, launch, state, Attempts: 0, LastError: null,
            PredecessorInstanceId: predecessor, ResumingAt: state == ResumeState.Resuming ? stamp : null,
            Participant: new ParticipantResume(LaunchKind.ReviewFlow, ["kcap-flow-result"], null, null, 7, 1_000, "round 1", stamp));
        f.Orch.ParticipantLedger!.Write(entry);

        return entry;
    }

    ResumeLedgerEntry Eligible(Func<ResumeLedgerEntry, ResumeLedgerEntry>? change = null) {
        var launch = new ResumeLaunchDescriptor(
            null, null, null, "/repo", Tmp.Path, WorkLocation.OwnedWorktree, null, null, null, null, null, null,
            false, "server", null, null, null, null, "flow-1", "reviewer");
        var entry = new ResumeLedgerEntry(
            1, "agent-1", "daemon-1", Now, Now, "cursor", "pty", "session-1", "vendor-1", launch, ResumeState.Resuming, 0,
            null, "instance-old", Now.AddMinutes(-1),
            Participant: new ParticipantResume(LaunchKind.ReviewFlow, null, null, null, 1, 0, null, null));

        return change?.Invoke(entry) ?? entry;
    }

    [Test]
    public async Task An_eligible_entry_has_no_ineligibility() =>
        await Assert.That(AgentOrchestrator.ParticipantIneligibility(Eligible(), "instance-old", Now, autoResume: true)).IsNull();

    [Test]
    [Arguments("auto_resume_off")]
    [Arguments("not_planned")]
    [Arguments("stale")]
    [Arguments("not_predecessor")]
    [Arguments("no_prior_instance")]
    [Arguments("worktree_missing")]
    [Arguments("session_missing")]
    public async Task Each_ineligibility_is_named(string reason) {
        var (entry, prior, auto) = reason switch {
            "auto_resume_off"   => (Eligible(), "instance-old", false),
            "not_planned"       => (Eligible(e => e with { State = ResumeState.Running }), "instance-old", true),
            "stale"             => (Eligible(e => e with { ResumingAt = Now.AddMinutes(-16) }), "instance-old", true),
            "not_predecessor"   => (Eligible(), "instance-between", true),
            "no_prior_instance" => (Eligible(), (string?) null, true),
            "worktree_missing"  => (Eligible(e => e with { Launch = e.Launch with { WorktreePath = "/no/such/worktree" } }), "instance-old", true),
            _                   => (Eligible(e => e with { VendorSession = null }), "instance-old", true)
        };

        var expected = reason == "no_prior_instance" ? "not_predecessor" : reason;
        await Assert.That(AgentOrchestrator.ParticipantIneligibility(entry, prior, Now, auto)).IsEqualTo(expected);
    }

    [Test]
    public async Task An_eligible_participant_is_resumed_ahead_of_interactive_entries() {
        var f = Build();
        await using var _ = f.Orch;
        Participant(f, "reviewer-1");

        f.Orch.LoadResumeLedger();
        await f.Orch.ResumeOnBootAsync(CancellationToken.None);

        await Assert.That(f.Factory.Contexts.Single().AgentId).IsEqualTo("reviewer-1");
        await Assert.That(f.Orch.GetAgentForTest("reviewer-1")).IsNotNull();
        await Assert.That(f.Orch.ParticipantLedger!.TryRead("reviewer-1")!.State).IsEqualTo(ResumeState.Running);
    }

    [Test]
    public async Task An_ineligible_participant_is_reported_failed_and_removed() {
        var f = Build();
        await using var _ = f.Orch;
        var entry = Participant(f, "reviewer-1", predecessor: "instance-between");

        f.Orch.LoadResumeLedger();
        await f.Orch.ResumeOnBootAsync(CancellationToken.None);

        await Assert.That(f.Server.ShutdownReports.Select(r => (r.AgentId, r.Status, r.Reason)))
            .IsEquivalentTo([("reviewer-1", "Failed", "resume_failed:not_predecessor")]);
        await Assert.That(f.Orch.ParticipantLedger!.TryRead("reviewer-1")).IsNull();
        await Assert.That(Directory.Exists(entry.Launch.WorktreePath)).IsFalse();
        await Assert.That(f.Factory.Contexts).IsEmpty();
    }

    [Test]
    public async Task A_failed_participant_resume_never_runs_kcap_continue() {
        var f = Build(new ReviewerFactory { Fails = true });
        await using var _ = f.Orch;
        f.Orch.ContinueEntry = (_, _, _) => throw new InvalidOperationException("a participant must not continue");
        Participant(f, "reviewer-1");

        f.Orch.LoadResumeLedger();
        await f.Orch.ResumeOnBootAsync(CancellationToken.None);

        await Assert.That(f.Server.ShutdownReports.Single().Reason).StartsWith("resume_failed:");
        await Assert.That(f.Orch.ParticipantLedger!.TryRead("reviewer-1")).IsNull();
    }

    [Test]
    public async Task An_unresolved_predecessor_leaves_a_tombstone_until_it_resolves() {
        var f = Build();
        await using var _ = f.Orch;
        var entry = Participant(f, "reviewer-1", state: ResumeState.Running);
        f.Orch.WritePidRecordForTest(new AgentPidRecord(
            "reviewer-1", 999_999, "", PidIdentityKind.IdentityUnavailable, "ReviewFlow", "cursor", "flow-1", "reviewer",
            f.Orch.DaemonIdForTest, "previous-epoch", TimeProvider.System.GetUtcNow()));

        f.Orch.LoadResumeLedger();
        await f.Orch.ResumeOnBootAsync(CancellationToken.None);

        await Assert.That(f.Server.ShutdownReports.Single().Reason).IsEqualTo("resume_failed:not_planned");
        await Assert.That(f.Orch.ParticipantLedger!.TryRead("reviewer-1")!.LastError).IsEqualTo("tombstone:not_planned");
        await Assert.That(Directory.Exists(entry.Launch.WorktreePath)).IsTrue();

        f.Orch.DeletePidRecordForTest("reviewer-1");
        await f.Orch.SweepParticipantTombstonesAsync();

        await Assert.That(f.Orch.ParticipantLedger!.TryRead("reviewer-1")).IsNull();
        await Assert.That(Directory.Exists(entry.Launch.WorktreePath)).IsFalse();
    }

    [Test]
    public async Task A_participant_entry_is_refused_by_agent_resume() {
        var f = Build();
        await using var _ = f.Orch;
        Participant(f, "reviewer-1", state: ResumeState.Interrupted);
        f.Orch.LoadResumeLedger();

        await Assert.That(await f.Orch.ResumeInterruptedAsync("reviewer-1", CancellationToken.None)).IsEqualTo("not_interrupted");
    }

    [Test]
    public async Task A_resumed_participant_launches_as_the_reviewer_it_was() {
        var f = Build();
        await using var _ = f.Orch;
        var entry = Participant(f, "reviewer-1");

        f.Orch.LoadResumeLedger();
        await f.Orch.ResumeOnBootAsync(CancellationToken.None);

        var ctx = f.Factory.Contexts.Single();
        await Assert.That(ctx.IsReviewFlow).IsTrue();
        await Assert.That(ctx.McpAllowlist!).IsEquivalentTo(["kcap-flow-result"]);
        await Assert.That(ctx.ResumeSessionId).IsEqualTo("vendor-session-1");
        await Assert.That(ctx.Prompt).IsEqualTo(ReviewerResumeNudge.Build(entry.ResumingAt!.Value, "round 1"));
        await Assert.That(ctx.Worktree.Path).IsEqualTo(entry.Launch.WorktreePath);
        await Assert.That(ctx.FlowRole).IsEqualTo("reviewer");

        var agent = f.Orch.GetAgentForTest("reviewer-1")!;
        await Assert.That(agent.Kind).IsEqualTo(LaunchKind.ReviewFlow);
        await Assert.That(agent.FlowRunId).IsEqualTo("flow-1");
    }

    /// <summary>A reviewer runs on the default account and its entry records that account; naming it on the
    /// relaunch would trip the refusal of a review-flow launch that names an account.</summary>
    [Test]
    public async Task A_participant_recorded_on_the_default_account_resumes() {
        var f = Build();
        await using var _ = f.Orch;
        Participant(f, "reviewer-1", accountId: "account-default");

        f.Orch.LoadResumeLedger();
        await f.Orch.ResumeOnBootAsync(CancellationToken.None);

        await Assert.That(f.Factory.Contexts.Single().AgentId).IsEqualTo("reviewer-1");
        await Assert.That(f.Orch.GetAgentForTest("reviewer-1")).IsNotNull();
        await Assert.That(f.Server.ShutdownReports).IsEmpty();
    }

    [Test]
    public async Task A_resumed_participant_continues_its_predecessors_clock() {
        var f = Build();
        await using var _ = f.Orch;
        Participant(f, "reviewer-1");

        f.Orch.LoadResumeLedger();
        await f.Orch.ResumeOnBootAsync(CancellationToken.None);

        var clock = f.Orch.GetAgentForTest("reviewer-1")!.ActivityClock.Snapshot();
        await Assert.That(clock.ActivitySeq).IsGreaterThan(7UL);
        await Assert.That(clock.AgeMs).IsGreaterThanOrEqualTo(1_000UL);
    }

    [Test]
    public async Task A_certification_the_successor_does_not_meet_refuses_the_resume_before_spawn() {
        var f = Build();
        await using var _ = f.Orch;
        var entry = Participant(f, "reviewer-1");
        f.Orch.ParticipantLedger!.Write(entry with {
            Participant = entry.Participant! with {
                Certification = new ReviewerCertificationRequirement("claude", ">=1", "policy", "rev", "", "")
            }
        });

        f.Orch.LoadResumeLedger();
        await f.Orch.ResumeOnBootAsync(CancellationToken.None);

        await Assert.That(f.Factory.Contexts).IsEmpty();
        await Assert.That(f.Server.ShutdownReports.Single().Reason).StartsWith("resume_failed:");
    }

    static SendInputCommand Round(string text) => new("reviewer-1", text, null, Guid.NewGuid());

    async Task<(Fixture F, Task Lane)> StartHandshake(bool fails = false) {
        var f = Build(new ReviewerFactory { Gate = new TaskCompletionSource(), Fails = fails });
        Participant(f, "reviewer-1");
        f.Orch.LoadResumeLedger();
        var lane = f.Orch.ResumeOnBootAsync(CancellationToken.None);
        await WaitHarness.PollUntilAsync(() => !f.Factory.Contexts.IsEmpty);

        return (f, lane);
    }

    [Test]
    public async Task A_round_sent_during_the_handshake_is_delivered_after_publication() {
        var (f, lane) = await StartHandshake();
        await using var _ = f.Orch;

        await f.Orch.HandleSendInputForTest(Round("round 2"));
        f.Factory.Gate!.SetResult();
        await WaitHarness.WaitBoundedAsync(lane, "the resume lane");

        await Assert.That(f.Server.InputRejections).IsEmpty();
        await Assert.That(f.Factory.LastRuntime!.UserInputs.Any(i => i.Contains("round 2"))).IsTrue();
    }

    [Test]
    public async Task A_round_for_a_participant_whose_resume_failed_is_reported_unknown_agent() {
        var (f, lane) = await StartHandshake(fails: true);
        await using var _ = f.Orch;
        var round = Round("round 2");

        await f.Orch.HandleSendInputForTest(round);
        f.Factory.Gate!.SetResult();
        await WaitHarness.WaitBoundedAsync(lane, "the resume lane");

        await Assert.That(f.Server.InputRejections).IsEquivalentTo([(round.DispatchId!.Value, "reviewer-1", "unknown_agent")]);
    }

    [Test]
    public async Task The_ninth_held_round_is_reported_queue_full() {
        var (f, lane) = await StartHandshake();
        await using var _ = f.Orch;

        for (var i = 0; i < 9; i++) await f.Orch.HandleSendInputForTest(Round($"round {i}"));

        await Assert.That(f.Server.InputRejections.Select(r => r.Reason)).IsEquivalentTo(["queue_full"]);
        f.Factory.Gate!.SetResult();
        await WaitHarness.WaitBoundedAsync(lane, "the resume lane");
    }

    [Test]
    public async Task A_server_stop_withdraws_a_participant_that_is_still_resuming() {
        var (f, lane) = await StartHandshake();
        await using var _ = f.Orch;

        await f.Orch.HandleStopAgent("reviewer-1");
        f.Factory.Gate!.TrySetResult();
        await WaitHarness.WaitBoundedAsync(lane, "the resume lane");

        await Assert.That(f.Orch.GetAgentForTest("reviewer-1")).IsNull();
        await Assert.That(f.Orch.ParticipantLedger!.TryRead("reviewer-1")).IsNull();
    }

    [Test]
    public async Task An_end_agents_shutdown_withdraws_participants_still_resuming() {
        var (f, lane) = await StartHandshake();
        var queued = Participant(f, "reviewer-2");
        f.Orch.ShutdownIntent = ShutdownIntent.EndAgents;

        await f.Orch.DisposeAsync();
        f.Factory.Gate!.TrySetResult();
        await WaitHarness.WaitBoundedAsync(lane, "the resume lane");

        await Assert.That(f.Orch.GetAgentForTest("reviewer-1")).IsNull();
        await Assert.That(f.Orch.ParticipantLedger!.TryRead("reviewer-1")).IsNull();
        await Assert.That(f.Orch.ParticipantLedger!.TryRead(queued.AgentId)).IsNull();
    }

    [Test]
    public async Task Startup_is_incomplete_while_a_participant_is_unpublished_and_complete_after() {
        var f = Build(new ReviewerFactory { Gate = new TaskCompletionSource() });
        await using var _ = f.Orch;
        await f.Orch.ReapOrphansOnceAsync();
        var baseline = f.Orch.ComputeStartupReapComplete();
        await Assert.That(baseline).IsTrue();
        Participant(f, "reviewer-1");
        f.Orch.LoadResumeLedger();
        var lane = f.Orch.ResumeOnBootAsync(CancellationToken.None);
        await WaitHarness.PollUntilAsync(() => !f.Factory.Contexts.IsEmpty);

        await Assert.That(f.Orch.ComputeStartupReapComplete()).IsFalse();
        await Assert.That(f.Orch.TakeRecoverySnapshot().StartupReapComplete).IsFalse();

        f.Factory.Gate!.SetResult();
        await WaitHarness.WaitBoundedAsync(lane, "the resume lane");

        await Assert.That(f.Orch.ComputeStartupReapComplete()).IsEqualTo(baseline);
    }

    [Test]
    public async Task The_deadline_fails_a_stalled_participant_and_lifts_the_hold() {
        var f = Build(new ReviewerFactory { Gate = new TaskCompletionSource() });
        await using var _ = f.Orch;
        f.Orch.ParticipantRecoveryDeadline = TimeSpan.FromMilliseconds(200);
        await f.Orch.ReapOrphansOnceAsync();
        var baseline = f.Orch.ComputeStartupReapComplete();
        await Assert.That(baseline).IsTrue();
        Participant(f, "reviewer-1");
        f.Orch.LoadResumeLedger();
        var lane = f.Orch.ResumeOnBootAsync(CancellationToken.None);

        await WaitHarness.PollUntilAsync(() => f.Server.ShutdownReports.Count == 1);
        f.Factory.Gate!.TrySetResult();
        await WaitHarness.WaitBoundedAsync(lane, "the resume lane");

        await Assert.That(f.Server.ShutdownReports.Single().Reason).IsEqualTo("resume_failed:recovery_deadline");
        await Assert.That(f.Orch.GetAgentForTest("reviewer-1")).IsNull();
        await Assert.That(f.Orch.ParticipantLedger!.TryRead("reviewer-1")).IsNull();
        await Assert.That(f.Orch.ComputeStartupReapComplete()).IsEqualTo(baseline);
    }

    /// <summary>The deadline also covers a participant that has published but whose registration or
    /// readiness has not settled: it is stopped and reported failed, so it cannot hold the lane. The failure
    /// goes out after the stop's own status, which would otherwise clear it.</summary>
    [Test]
    public async Task The_deadline_stops_a_published_participant_whose_resume_has_not_settled() {
        var f = Build(configure: c => c.ResumeReadinessWindow = TimeSpan.FromMinutes(5));
        await using var _ = f.Orch;
        f.Orch.ParticipantRecoveryDeadline = TimeSpan.FromMilliseconds(300);
        f.Server.ShutdownReportGate = new TaskCompletionSource();
        Participant(f, "reviewer-1");
        f.Orch.LoadResumeLedger();
        var lane = f.Orch.ResumeOnBootAsync(CancellationToken.None);

        await WaitHarness.PollUntilAsync(() => f.Server.ShutdownReports.Count == 1);
        var completedBeforeFailure = f.Server.StatusChangedCalls.Contains(("reviewer-1", "Completed"));
        f.Server.ShutdownReportGate.SetResult();
        await WaitHarness.WaitBoundedAsync(lane, "the resume lane");
        await WaitHarness.PollUntilAsync(() => f.Orch.GetAgentForTest("reviewer-1") is null);

        await Assert.That(f.Server.ShutdownReports.Single().Reason).IsEqualTo("resume_failed:recovery_deadline");
        await Assert.That(completedBeforeFailure).IsTrue();
        await Assert.That(f.Orch.ParticipantLedger!.TryRead("reviewer-1")).IsNull();
    }

    [Test]
    public async Task No_snapshot_reports_complete_while_a_participant_is_in_neither_list() {
        var f = Build(new ReviewerFactory { Gate = new TaskCompletionSource() });
        await using var _ = f.Orch;
        await f.Orch.ReapOrphansOnceAsync();
        Participant(f, "reviewer-1");
        f.Orch.LoadResumeLedger();
        var lane = f.Orch.ResumeOnBootAsync(CancellationToken.None);
        await WaitHarness.PollUntilAsync(() => !f.Factory.Contexts.IsEmpty);

        var torn = 0;
        using var stop = new CancellationTokenSource();
        var reader = Task.Run(() => {
            while (!stop.IsCancellationRequested) {
                var snap = f.Orch.TakeRecoverySnapshot();
                if (snap.StartupReapComplete && !snap.LiveIds.Contains("reviewer-1") && snap.Claims.All(c => c.AgentId != "reviewer-1")
                 && f.Orch.ParticipantLedger!.TryRead("reviewer-1") is { State: ResumeState.Resuming })
                    Interlocked.Increment(ref torn);
            }
        });

        f.Factory.Gate!.SetResult();
        await WaitHarness.WaitBoundedAsync(lane, "the resume lane");
        await stop.CancelAsync();
        await reader;

        await Assert.That(torn).IsEqualTo(0);
        await Assert.That(f.Orch.TakeRecoverySnapshot().StartupReapComplete).IsTrue();
    }

    static async Task<LocalFrame> LocalStop(AgentOrchestrator orch, bool force, string agentId) {
        using var stream = new MemoryStream();
        await orch.HandleLocalStopV2Async(force, agentId, stream, CancellationToken.None);
        stream.Position = 0;

        return (await FrameCodec.ReadAsync(stream, CancellationToken.None))!;
    }

    [Test]
    public async Task A_resuming_participant_is_listed_as_a_flow_agent() {
        var (f, lane) = await StartHandshake();
        await using var _ = f.Orch;

        var row = f.Orch.SnapshotAgentsForStatus().Single(r => r.Id == "reviewer-1");

        await Assert.That(row.Kind).IsEqualTo("review-flow");
        f.Factory.Gate!.SetResult();
        await WaitHarness.WaitBoundedAsync(lane, "the resume lane");
    }

    [Test]
    public async Task A_resuming_participant_is_listed_by_kcap_agent_ls_as_a_flow_agent() {
        var (f, lane) = await StartHandshake();
        await using var _ = f.Orch;

        using var client = new DuplexTestStream(new MemoryStream(), new MemoryStream());
        await f.Orch.HandleLocalListAsync(withTitles: false, withState: true, client, default);
        client.WrittenStream.Position = 0;
        var reply = await FrameCodec.ReadAsync(client.WrittenStream, default);

        await Assert.That(reply!.Text.Split('\n').Select(l => l.Split('\t')).Single(r => r[0] == "reviewer-1")[3]).IsEqualTo("review-flow");
        f.Factory.Gate!.SetResult();
        await WaitHarness.WaitBoundedAsync(lane, "the resume lane");
    }

    [Test]
    public async Task An_unforced_stop_refuses_a_resuming_participant_and_keeps_its_recovery() {
        var (f, lane) = await StartHandshake();
        await using var _ = f.Orch;

        var reply = await LocalStop(f.Orch, force: false, "reviewer-1");

        await Assert.That(reply.Type).IsEqualTo(FrameType.Error);
        await Assert.That(reply.Text).Contains("--force");
        await Assert.That(f.Orch.ParticipantLedger!.TryRead("reviewer-1")).IsNotNull();

        f.Factory.Gate!.SetResult();
        await WaitHarness.WaitBoundedAsync(lane, "the resume lane");
        await Assert.That(f.Orch.GetAgentForTest("reviewer-1")).IsNotNull();
    }

    [Test]
    public async Task A_forced_stop_withdraws_a_resuming_participant() {
        var (f, lane) = await StartHandshake();
        await using var _ = f.Orch;

        var reply = await LocalStop(f.Orch, force: true, "reviewer-1");
        f.Factory.Gate!.TrySetResult();
        await WaitHarness.WaitBoundedAsync(lane, "the resume lane");

        await Assert.That(reply.Type).IsEqualTo(FrameType.StopAck);
        await Assert.That(f.Orch.GetAgentForTest("reviewer-1")).IsNull();
        await Assert.That(f.Orch.ParticipantLedger!.TryRead("reviewer-1")).IsNull();
    }

    [Test]
    public async Task Stop_all_skips_a_resuming_participant_unless_forced() {
        var (f, lane) = await StartHandshake();
        await using var _ = f.Orch;

        var skipped = await LocalStop(f.Orch, force: false, "");
        await Assert.That(skipped.Text).Contains("reviewer-1\tskipped");
        await Assert.That(f.Orch.ParticipantLedger!.TryRead("reviewer-1")).IsNotNull();

        var forced = await LocalStop(f.Orch, force: true, "");
        f.Factory.Gate!.TrySetResult();
        await WaitHarness.WaitBoundedAsync(lane, "the resume lane");

        await Assert.That(forced.Text).Contains("reviewer-1\tstopped");
        await Assert.That(f.Orch.GetAgentForTest("reviewer-1")).IsNull();
    }
}
