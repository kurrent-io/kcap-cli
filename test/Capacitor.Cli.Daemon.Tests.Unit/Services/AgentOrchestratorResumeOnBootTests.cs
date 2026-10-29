using System.Collections.Concurrent;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Resume;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TUnit.Core.Enums;

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
        public bool ResumesIdle { get; init; }
        public bool ResumesIdleWithoutPrompt(string? runtimeTransport) => ResumesIdle;

        public ConcurrentQueue<RuntimeStartContext> Contexts { get; } = new();
        public TaskCompletionSource? Gate { get; set; }
        public bool ExitBeforeHandshake { get; init; }
        public bool PtyStyle { get; init; }
        public string? ResumeToken { get; init; }
        public bool ReopensSession { get; init; }
        public FakeAcpRuntime? LastAcpRuntime { get; private set; }
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

                var runtime = ReopensSession && ctx.ResumeSessionId is { } reopened
                    ? new FakeAcpRuntime { Vendor = Vendor, Cwd = ctx.Worktree.Path, AcpSessionId = reopened, Reopened = true }
                    : new FakeAcpRuntime { Vendor = Vendor, Cwd = ctx.Worktree.Path, VendorResumeToken = ResumeToken };
                LastAcpRuntime = runtime;

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

    Fixture Build(ResumableFactory? factory = null, Action<DaemonConfig>? configure = null, TimeProvider? time = null,
                  CaptureServerConnection? server = null) {
        var repo   = Tmp.CreateDir("repo");
        server ??= new CaptureServerConnection();
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
            string vendor = "cursor", string? permissionMode = null, string title = "the thing", bool? titleDerived = null,
            IReadOnlyList<string>? inherited = null, bool? awaitingInput = null) {
        var stamp = at ?? TimeProvider.System.GetUtcNow();
        var launch = new ResumeLaunchDescriptor(
            Prompt: "build the thing", Model: "auto", Effort: null, RepoPath: f.Repo,
            WorktreePath: worktree ?? Tmp.CreateDir($"wt-{agentId}"), Work: WorkLocation.OwnedWorktree,
            GitRelativeCwd: null, SandboxPolicy: null, ApprovalPolicy: null, PermissionPreset: null,
            McpConfigPath: null, Title: title, IsPrivate: isPrivate, Origin: "server",
            RequesterUserId: "user-1", RequesterDisplay: "User One", ConsentSource: "default",
            ConsentAt: stamp.ToString("O"), FlowRunId: null, FlowRole: null,
            WorktreeBranch: $"capacitor/custom-{agentId}", WorktreeIsStandalone: standalone, WorktreeSourceRepo: f.Repo,
            PermissionMode: permissionMode, TitleDerived: titleDerived);
        var entry = new ResumeLedgerEntry(
            ResumeLedgerEntry.CurrentSchemaVersion, agentId, "daemon-1", stamp, stamp, vendor, "acp",
            $"session-{agentId}", vendorSession, launch, state, Attempts: 0, LastError: null,
            PredecessorInstanceId: predecessor, ResumingAt: state == ResumeState.Resuming ? stamp : null,
            DaemonInstanceId: daemonInstanceId, InheritedAgentIds: inherited, AwaitingInput: awaitingInput);
        f.Orch.ResumeLedger!.Write(entry);

        return entry;
    }

    /// <summary>An agent that was waiting comes back waiting, with nothing sent, but only where the runtime can resume
    /// without a turn. Mid-turn, an entry from an older daemon that recorded no wait, or a runtime that needs a first turn
    /// all get the nudge.</summary>
    [Test]
    [Arguments(true, true, false)]
    [Arguments(true, false, true)]
    [Arguments(true, null, true)]
    [Arguments(false, true, true)]
    public async Task Only_a_waiting_agent_on_a_runtime_that_resumes_without_a_turn_comes_back_without_the_nudge(
            bool resumesIdle, bool? awaitingInput, bool nudged) {
        var f = Build(new ResumableFactory { PtyStyle = true, ResumesIdle = resumesIdle });
        await using var _ = f.Orch;
        var entry = Entry(f, "resume-idle", awaitingInput: awaitingInput);

        f.Orch.LoadResumeLedger();
        await f.Orch.ResumeOnBootAsync(CancellationToken.None);

        var ctx = f.Factory.Contexts.Single();
        await Assert.That(ctx.Prompt).IsEqualTo(nudged ? ResumeNudge.Build(entry.ResumingAt!.Value, "build the thing") : "");
        await Assert.That(f.Orch.GetAgentForTest("resume-idle")!.ActivityClock.AwaitingInput).IsEqualTo(!nudged);
        // The running entry holds no waiting state of its own: a crash before the next keep-agents shutdown must
        // not read a turn started since as waiting.
        await Assert.That(f.Orch.ResumeLedger!.TryRead("resume-idle")!.AwaitingInput).IsNull();
    }

    /// <summary>A silently resumed agent is waiting from the moment anything can reach it, so a turn that starts
    /// while the resume is still registering is not overwritten as waiting.</summary>
    [Test]
    public async Task A_turn_that_starts_while_a_silent_resume_registers_stays_working() {
        var server = new CaptureServerConnection {
            AgentRegisteredEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            AgentRegisteredGate    = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var f = Build(new ResumableFactory { PtyStyle = true, ResumesIdle = true }, server: server);
        await using var _ = f.Orch;
        Entry(f, "resume-busy", awaitingInput: true);

        f.Orch.LoadResumeLedger();
        var resuming = f.Orch.ResumeOnBootAsync(CancellationToken.None);
        await server.AgentRegisteredEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        f.Orch.GetAgentForTest("resume-busy")!.ActivityClock.SetAwaitingInput(false);
        server.AgentRegisteredGate.SetResult();
        await resuming;

        await Assert.That(f.Orch.GetAgentForTest("resume-busy")!.ActivityClock.AwaitingInput).IsFalse();
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
        await Assert.That(after.StartedAt?.UtcDateTime).IsEqualTo(agent!.CreatedAt);
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

    /// <summary>A resumed run is a new run: its epoch replaces the predecessor's, and no end committed for
    /// the predecessor's run carries over.</summary>
    [Test]
    public async Task A_resumed_entry_records_its_own_epoch_and_no_committed_end() {
        var f = Build();
        await using var _ = f.Orch;
        var entry = Entry(f, "resume-epoch");
        f.Orch.ResumeLedger!.Write(entry with { RunEpoch = "epoch-old", LaunchEpoch = "epoch-old" });

        f.Orch.LoadResumeLedger();
        await f.Orch.ResumeOnBootAsync(CancellationToken.None);

        var after = f.Orch.ResumeLedger!.TryRead("resume-epoch")!;
        await Assert.That(after.RunEpoch).IsEqualTo(f.Orch.DaemonEpochForTest);
        await Assert.That(after.LaunchEpoch).IsEqualTo("epoch-old");
        await Assert.That(after.EndCommittedAt).IsNull();
    }

    /// <summary>A start title cut from the prompt stays a placeholder on resume: re-registered as the caller's,
    /// it would outrank the title the session has since been given.</summary>
    [Test]
    public async Task A_resumed_prompt_cut_title_registers_as_derived() {
        var f = Build();
        await using var _ = f.Orch;
        Entry(f, "resume-derived", title: "build the thing", titleDerived: true);

        f.Orch.LoadResumeLedger();
        await f.Orch.ResumeOnBootAsync(CancellationToken.None);

        var agent = f.Orch.GetAgentForTest("resume-derived")!;
        await Assert.That(agent.StartTitle).IsEqualTo(new AgentStartTitle("build the thing", Derived: true));
        await Assert.That(f.Orch.ResumeLedger!.TryRead("resume-derived")!.Launch.TitleDerived ?? false).IsTrue();
    }

    [Test]
    public async Task A_resumed_explicit_title_stays_the_callers() {
        var f = Build();
        await using var _ = f.Orch;
        Entry(f, "resume-explicit", title: "Named by the caller", titleDerived: false);

        f.Orch.LoadResumeLedger();
        await f.Orch.ResumeOnBootAsync(CancellationToken.None);

        var agent = f.Orch.GetAgentForTest("resume-explicit")!;
        await Assert.That(agent.StartTitle).IsEqualTo(new AgentStartTitle("Named by the caller", Derived: false));
    }

    /// <summary>An entry without the flag cannot say which kind its title is, so it resumes as derived: a resumed
    /// session already holds its start title, and a placeholder never displaces it.</summary>
    [Test]
    public async Task A_resumed_title_without_the_flag_registers_as_derived() {
        var f = Build();
        await using var _ = f.Orch;
        Entry(f, "resume-legacy", title: "build the thing", titleDerived: null);

        f.Orch.LoadResumeLedger();
        await f.Orch.ResumeOnBootAsync(CancellationToken.None);

        var agent = f.Orch.GetAgentForTest("resume-legacy")!;
        await Assert.That(agent.StartTitle).IsEqualTo(new AgentStartTitle("build the thing", Derived: true));
    }

    /// <summary>A native resume keeps its agent id, so an agent that was itself a successor goes on naming the agent it
    /// continued, the last one its ledger inherited; an agent that inherited nothing names none.</summary>
    [Test]
    [Arguments("", null)]
    [Arguments("first-agent,previous-agent", "previous-agent")]
    public async Task A_native_resume_announces_the_agent_its_ledger_says_it_continues(string inherited, string? continues) {
        var f = Build();
        await using var _ = f.Orch;
        Entry(f, "resume-lineage", inherited: inherited.Length == 0 ? null : inherited.Split(','));

        f.Orch.LoadResumeLedger();
        await f.Orch.ResumeOnBootAsync(CancellationToken.None);

        await Assert.That(f.Server.AgentRegisteredContinues.Single(r => r.AgentId == "resume-lineage").ContinuesAgentId)
            .IsEqualTo(continues);
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

        var discard = f.Orch.DiscardResumeEntry("resume-live");

        await Assert.That(discard.Discarded).IsFalse();
        await Assert.That(discard.Live).IsSameReferenceAs(f.Orch.GetAgentForTest("resume-live"));
        await Assert.That(f.Orch.ResumeLedger!.TryRead("resume-live")).IsNotNull();
    }

    [Test]
    [ExcludeOn(TUnit.Core.Enums.OS.Windows), System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task A_discard_whose_delete_fails_keeps_the_candidate_listed() {
        var f = Build();
        await using var _ = f.Orch;
        Entry(f, "resume-ro");
        f.Orch.LoadResumeLedger();
        var dir  = Path.GetDirectoryName(f.Orch.ResumeLedger!.EntryPath("resume-ro"))!;
        var mode = File.GetUnixFileMode(dir);
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        bool discarded;

        try {
            discarded = f.Orch.TryDiscardResumeEntry("resume-ro");
        } finally {
            File.SetUnixFileMode(dir, mode);
        }

        await Assert.That(discarded).IsFalse();
        await Assert.That(f.Orch.ResumeLedger!.TryRead("resume-ro")).IsNotNull();
        await Assert.That(f.Orch.LedgerOnlyAgents().Select(r => r.Entry.AgentId)).Contains("resume-ro");
        await Assert.That(f.Orch.ResumingClaims().Select(c => c.AgentId)).Contains("resume-ro");
    }

    /// <summary>An unreadable ledger leaves the keep-sets empty, so the boot must know not to sweep against them.</summary>
    [Test]
    [ExcludeOn(OS.Windows), System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task LoadResumeLedger_reports_an_unreadable_ledger() {
        var f = Build();
        await using var _ = f.Orch;
        Entry(f, "unreadable-1");
        var dir  = Path.GetDirectoryName(f.Orch.ResumeLedger!.RingPath("unreadable-1"))!;
        var mode = File.GetUnixFileMode(dir);
        File.SetUnixFileMode(dir, UnixFileMode.None);

        try {
            await Assert.That(f.Orch.LoadResumeLedger()).IsFalse();
            await Assert.That(f.Orch.LedgerWorktreePaths()).IsEmpty();
        } finally {
            File.SetUnixFileMode(dir, mode);
        }

        await Assert.That(f.Orch.LoadResumeLedger()).IsTrue();
        await Assert.That(f.Orch.LedgerWorktreePaths().Count).IsEqualTo(1);
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

    /// <summary>A runtime whose resume takes something other than the session id records that token, and
    /// the next resume of the entry hands it back.</summary>
    [Test]
    public async Task A_runtimes_resume_token_is_recorded_and_handed_to_the_next_resume() {
        var f = Build(new ResumableFactory { ResumeToken = "/sessions/pi-1.jsonl" });
        await using var _ = f.Orch;
        Entry(f, "pi-1");

        f.Orch.LoadResumeLedger();
        await f.Orch.ResumeOnBootAsync(CancellationToken.None);

        await Assert.That(f.Orch.GetAgentForTest("pi-1")!.VendorSession).IsEqualTo("/sessions/pi-1.jsonl");
        var recorded = f.Orch.ResumeLedger!.TryRead("pi-1")!;
        await Assert.That(recorded.VendorSession).IsEqualTo("/sessions/pi-1.jsonl");

        var next = Build(new ResumableFactory { ResumeToken = "/sessions/pi-1.jsonl" });
        await using var __ = next.Orch;
        next.Orch.ResumeLedger!.Write(recorded with { State = ResumeState.Resuming, ResumingAt = recorded.UpdatedAt });

        next.Orch.LoadResumeLedger();
        await next.Orch.ResumeOnBootAsync(CancellationToken.None);

        await Assert.That(next.Factory.Contexts.Single().ResumeSessionId).IsEqualTo("/sessions/pi-1.jsonl");
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

    /// <summary>A zero age cap means no cap, as the environment knob documents.</summary>
    [Test]
    public async Task A_zero_age_cap_resumes_an_entry_of_any_age() {
        var f = Build(configure: c => c.ResumeMaxAge = TimeSpan.Zero);
        await using var _ = f.Orch;
        Entry(f, "ancient-1", state: ResumeState.Running, at: TimeProvider.System.GetUtcNow() - TimeSpan.FromDays(400));

        f.Orch.LoadResumeLedger();
        await f.Orch.ResumeOnBootAsync(CancellationToken.None);

        await Assert.That(f.Factory.Contexts.Count).IsEqualTo(1);
        await Assert.That(f.Orch.ResumeLedger!.TryRead("ancient-1")!.State).IsEqualTo(ResumeState.Running);
    }

    /// <summary>A heartbeat file outlives the incarnation that wrote it, so an entry written after the
    /// last beat is the newer evidence of life.</summary>
    [Test]
    public async Task A_recent_entry_behind_a_stale_heartbeat_is_resumed() {
        var time = TimeProvider.System;
        var f = Build();
        await using var _ = f.Orch;
        Entry(f, "fresh-1", state: ResumeState.Running, at: time.GetUtcNow() - TimeSpan.FromSeconds(30));
        f.Config.Store.EnsureDirectory();
        var heartbeat = f.Config.Store.HeartbeatPath(f.Config.Name);
        await File.WriteAllBytesAsync(heartbeat, []);
        File.SetLastWriteTimeUtc(heartbeat, (time.GetUtcNow() - f.Config.ResumeMaxAge - TimeSpan.FromHours(1)).UtcDateTime);

        f.Orch.LoadResumeLedger();
        await f.Orch.ResumeOnBootAsync(CancellationToken.None);

        await Assert.That(f.Factory.Contexts.Single().AgentId).IsEqualTo("fresh-1");
        await Assert.That(f.Orch.ResumeLedger!.TryRead("fresh-1")!.State).IsNotEqualTo(ResumeState.Interrupted);
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

    /// <summary>A stop of an entry with nothing in flight removes what a normal stop removes; the entry's
    /// worktree is standalone so the removal needs no git checkout.</summary>
    [Test]
    public async Task A_server_stop_of_a_waiting_candidate_removes_its_worktree_and_attachments() {
        var factory = new ResumableFactory { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var f = Build(factory, c => c.ResumeConcurrency = 1);
        await using var _ = f.Orch;
        var first  = Entry(f, "release-1", standalone: true);
        var second = Entry(f, "release-2", standalone: true);
        Directory.CreateDirectory(f.Orch.AttachmentStore.DirectoryFor("release-1"));
        Directory.CreateDirectory(f.Orch.AttachmentStore.DirectoryFor("release-2"));

        f.Orch.LoadResumeLedger();
        var lane = f.Orch.ResumeOnBootAsync(CancellationToken.None);
        await WaitHarness.PollUntilAsync(() => factory.InFlight == 1);
        var waiting = factory.Contexts.Single().AgentId == "release-1" ? second : first;

        await f.Orch.HandleServerStopAgentForTest(waiting.AgentId);

        await Assert.That(Directory.Exists(waiting.Launch.WorktreePath)).IsFalse();
        await Assert.That(Directory.Exists(f.Orch.AttachmentStore.DirectoryFor(waiting.AgentId))).IsFalse();

        factory.Gate.SetResult();
        await WaitHarness.WaitBoundedAsync(lane, "the resume lane");
    }

    /// <summary>A stop while the runtime is still handshaking finds no published agent to mark; the
    /// attempt itself has to give up, or it would publish the agent and rewrite its entry afterwards. The
    /// gate never opens: the stop has to end the start itself.</summary>
    [Test]
    public async Task A_stop_while_the_runtime_handshakes_publishes_nothing_and_writes_no_entry() {
        var factory = new ResumableFactory { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var f = Build(factory);
        await using var _ = f.Orch;
        var entry = Entry(f, "handshake-1", standalone: true);
        var continued = 0;
        f.Orch.ContinueEntry = (_, _, _) => {
            Interlocked.Increment(ref continued);
            return Task.FromResult(ResumeOutcome.Interrupted);
        };

        f.Orch.LoadResumeLedger();
        var lane = f.Orch.ResumeOnBootAsync(CancellationToken.None);
        await WaitHarness.PollUntilAsync(() => factory.InFlight == 1);

        await f.Orch.HandleStopAgent("handshake-1");
        await WaitHarness.WaitBoundedAsync(lane, "the resume lane");

        await Assert.That(f.Orch.GetAgentForTest("handshake-1")).IsNull();
        await Assert.That(f.Orch.ResumeLedger!.TryRead("handshake-1")).IsNull();
        await Assert.That(continued).IsEqualTo(0);
        await Assert.That(f.Server.ShutdownReports.Any(r => r.Reason.StartsWith("resume_failed", StringComparison.Ordinal))).IsFalse();
        await Assert.That(f.Orch.LedgerOnlyAgents()).IsEmpty();
        await Assert.That(Directory.Exists(entry.Launch.WorktreePath)).IsFalse();
    }

    /// <summary>The host stop cancels the resume lane before the orchestrator's own shutdown runs, so the
    /// end-agents withdrawal finds neither candidate nor attempt; the entry still has to go.</summary>
    [Test]
    public async Task An_end_agents_shutdown_after_the_lane_was_cancelled_still_deletes_the_entry() {
        var factory = new ResumableFactory { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var f = Build(factory);
        await using var _ = f.Orch;
        Entry(f, "drained-1");
        using var stopping = new CancellationTokenSource();

        f.Orch.LoadResumeLedger();
        var lane = f.Orch.ResumeOnBootAsync(stopping.Token);
        await WaitHarness.PollUntilAsync(() => factory.InFlight == 1);

        f.Orch.ShutdownIntent = ShutdownIntent.EndAgents;
        await stopping.CancelAsync();
        await WaitHarness.WaitBoundedAsync(lane, "the resume lane");
        await f.Orch.DisposeAsync();

        await Assert.That(f.Orch.ResumeLedger!.ReadAll()).IsEmpty();
        await Assert.That(f.Server.LaunchFailedCalls).IsEmpty();
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

    /// <summary>A manual retry that lands while a failed run still reports keeps its own candidate: the
    /// failed run's teardown removes only what it registered.</summary>
    [Test]
    public async Task A_retry_during_a_failed_runs_report_keeps_its_candidate() {
        var f = Build(new ResumableFactory { ExitBeforeHandshake = true }, c => c.ResumeConcurrency = 2);
        await using var _ = f.Orch;
        Entry(f, "retry-1");
        f.Orch.ContinueEntry = (_, _, _) => Task.FromResult(ResumeOutcome.Interrupted);
        var report = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Server.ShutdownReportGate = report;

        f.Orch.LoadResumeLedger();
        var lane = f.Orch.ResumeOnBootAsync(CancellationToken.None);
        await WaitHarness.PollUntilAsync(() => { lock (f.Server.ShutdownReports) return f.Server.ShutdownReports.Count == 1; });

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Factory.Gate = start;
        await Assert.That(await f.Orch.ResumeInterruptedAsync("retry-1", CancellationToken.None)).IsEqualTo("resuming");
        await WaitHarness.PollUntilAsync(() => f.Factory.Contexts.Count == 2);

        report.SetResult();
        await WaitHarness.WaitBoundedAsync(lane, "the resume lane");

        await Assert.That(f.Orch.ResumingClaims().Select(c => c.AgentId)).Contains("retry-1");
        await Assert.That(f.Orch.LedgerOnlyAgents().Single(r => r.Entry.AgentId == "retry-1").Status).IsEqualTo("Resuming");

        await Assert.That(f.Orch.TryDiscardResumeEntry("retry-1")).IsTrue();
        start.SetResult();
    }

    /// <summary>The desktop app has no other view of an agent without a process: the status payload lists
    /// each ledger-only entry once, with its launch's identity and, when interrupted, the reason.</summary>
    [Test]
    public async Task The_status_snapshot_lists_resuming_and_interrupted_entries_with_the_reason() {
        var f = Build();
        await using var _ = f.Orch;
        Entry(f, "resume-1");
        var gone = Entry(f, "gone-1", state: ResumeState.Interrupted);
        f.Orch.ResumeLedger!.Write(gone with { LastError = "too_old", HasTerminal = false, TranscriptFormat = TranscriptFormats.Envelopes });

        f.Orch.LoadResumeLedger();
        var rows = f.Orch.SnapshotAgentsForStatus().ToDictionary(r => r.Id);

        await Assert.That(rows.Count).IsEqualTo(2);
        await Assert.That(rows["resume-1"].Status).IsEqualTo("Resuming");
        await Assert.That(rows["resume-1"].Interruption).IsNull();
        await Assert.That(rows["gone-1"].Status).IsEqualTo("Interrupted");
        await Assert.That(rows["gone-1"].Interruption).IsEqualTo("too_old");
        await Assert.That(rows["gone-1"].SessionId).IsEqualTo("session-gone-1");
        await Assert.That(rows["gone-1"].WorktreePath).IsEqualTo(gone.Launch.WorktreePath);
        await Assert.That(rows["gone-1"].Branch).IsEqualTo("capacitor/custom-gone-1");
        await Assert.That(rows["gone-1"].Title).IsEqualTo("the thing");
        await Assert.That(rows["gone-1"].Vendor).IsEqualTo("cursor");
        await Assert.That(rows["gone-1"].Kind).IsEqualTo("agent");
        await Assert.That(rows["gone-1"].HasTerminal).IsFalse();
        await Assert.That(rows["gone-1"].TranscriptFormat).IsEqualTo(TranscriptFormats.Envelopes);
    }

    /// <summary>A borrowed launch's row names its checkout as the live agent's does, so the rail keeps it in place.</summary>
    [Test]
    public async Task A_borrowed_entrys_status_row_reports_the_borrowed_checkout() {
        var f = Build();
        await using var _ = f.Orch;
        var entry = Entry(f, "borrowed-1", state: ResumeState.Interrupted);
        f.Orch.ResumeLedger!.Write(entry with { Launch = entry.Launch with { Work = WorkLocation.BorrowedCwd, WorktreePath = f.Repo } });

        f.Orch.LoadResumeLedger();
        var row = f.Orch.SnapshotAgentsForStatus().Single();

        await Assert.That(row.WorkLocation).IsEqualTo(WorkLocationText.Borrowed);
        await Assert.That(row.BorrowedFrom).IsEqualTo(f.Repo);
        await Assert.That(row.WorktreePath).IsEqualTo(f.Repo);
    }

    /// <summary>An interruption changes nothing else the status payload reads, so it must pulse on its own.</summary>
    [Test]
    public async Task An_interrupted_resume_pulses_the_status_notifier() {
        var f = Build(new ResumableFactory { ThrowsFromSupports = true });
        await using var _ = f.Orch;
        Entry(f, "faults-1");
        f.Orch.LoadResumeLedger();
        var before = f.Orch.StatusNotifierForTest.Version;

        await f.Orch.ResumeOnBootAsync(CancellationToken.None);

        await Assert.That(f.Orch.StatusNotifierForTest.Version).IsGreaterThan(before);
        await Assert.That(f.Orch.SnapshotAgentsForStatus().Single().Status).IsEqualTo("Interrupted");
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

    /// <summary>The server already holds the reopened session's events through its cursor, so the first
    /// new event must land after it: numbered from 0 it would be deduplicated away. The held first turn
    /// goes out only once the server has accepted that event.</summary>
    [Test]
    public async Task A_reopened_acp_session_forwards_from_the_servers_cursor_and_then_releases_its_first_turn() {
        var server = new CaptureServerConnection {
            AcpEventsAckOverride = batch => batch.Length == 0 ? new AcpBatchAck(41, 41) : new AcpBatchAck(batch[^1].Seq, batch[^1].Seq)
        };
        var f = Build(new ResumableFactory { ReopensSession = true }, c => c.ResumeReadinessWindow = TimeSpan.FromSeconds(3), server: server);
        await using var _ = f.Orch;
        Entry(f, "reopen-1");

        f.Orch.LoadResumeLedger();
        var lane = f.Orch.ResumeOnBootAsync(CancellationToken.None);

        await WaitHarness.PollUntilAsync(() => f.Orch.GetAgentForTest("reopen-1")?.AcpForwarder is not null);
        var runtime = f.Factory.LastAcpRuntime!;
        await Assert.That(runtime.ReleaseFirstTurnCalls).IsEqualTo(0);

        runtime.EnvelopesWriter.TryWrite(new AcpEventEnvelope { Kind = AcpEventKind.SystemNote, Text = "reopened" });
        await WaitHarness.PollUntilAsync(() => runtime.ReleaseFirstTurnCalls == 1);
        await WaitHarness.WaitBoundedAsync(lane, "the resume lane");

        var sent = server.AcpEventsCalls.Where(c => c.Envelopes.Length > 0).SelectMany(c => c.Envelopes).ToArray();
        await Assert.That(server.AcpEventsCalls[0].Envelopes).IsEmpty();
        await Assert.That(server.AcpEventsCalls[0].AcpSessionId).IsEqualTo("vendor-session-1");
        await Assert.That(sent.Select(e => e.Seq)).IsEquivalentTo([42L]);
        await Assert.That(sent.Any(e => e.Kind == AcpEventKind.SessionStarted)).IsFalse();
        await Assert.That(f.Orch.ResumeLedger!.TryRead("reopen-1")!.State).IsEqualTo(ResumeState.Running);
    }

    /// <summary>A server that takes nothing the reopened session says — a rejected cursor, a terminal
    /// binding dropping its first event, or no answer inside the readiness window — fails the native resume
    /// before its first turn is sent, so kcap continue cannot repeat anything it did.</summary>
    [Test]
    [Arguments("rejected")]
    [Arguments("terminal")]
    [Arguments("silent")]
    public async Task A_reopened_session_the_server_takes_no_events_for_falls_through_to_continue_without_its_first_turn(string refusal) {
        using var silence = new CancellationTokenSource();
        var server = new CaptureServerConnection {
            AcpEventsAckOverride = batch => refusal == "rejected"
                ? new AcpBatchAck(-1, -1, Rejected: true)
                : new AcpBatchAck(41, 41),
            AcpEventsBlockUntil = refusal == "silent" ? silence : null
        };
        var window = refusal == "silent" ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(30);
        var f = Build(new ResumableFactory { ReopensSession = true }, c => c.ResumeReadinessWindow = window, server: server);
        await using var _ = f.Orch;
        var entry = Entry(f, "terminal-1");
        var continued = new ConcurrentQueue<string>();
        f.Orch.ContinueEntry = (_, reason, _) => {
            continued.Enqueue(reason);
            return Task.FromResult(ResumeOutcome.Interrupted);
        };

        f.Orch.LoadResumeLedger();
        var lane = f.Orch.ResumeOnBootAsync(CancellationToken.None);
        if (refusal == "terminal") {
            await WaitHarness.PollUntilAsync(() => f.Orch.GetAgentForTest("terminal-1")?.AcpForwarder is not null);
            f.Factory.LastAcpRuntime!.EnvelopesWriter.TryWrite(
                new AcpEventEnvelope { Kind = AcpEventKind.SystemNote, Text = "reopened" });
        }

        await WaitHarness.WaitBoundedAsync(lane, "the resume lane");
        await silence.CancelAsync();

        await Assert.That(continued.Single()).IsEqualTo("acp_binding_unavailable");
        await Assert.That(f.Factory.LastAcpRuntime!.HasExited).IsTrue();
        await Assert.That(f.Factory.LastAcpRuntime!.ReleaseFirstTurnCalls).IsEqualTo(0);
        await Assert.That(Directory.Exists(entry.Launch.WorktreePath)).IsTrue();
        await Assert.That(f.Orch.ResumeLedger!.TryRead("terminal-1")!.LastError).IsEqualTo("acp_binding_unavailable");
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
        Entry(f, "attach-3", inherited: ["attach-0"]);
        var inheritedDir = Directory.CreateDirectory(f.Orch.AttachmentStore.DirectoryFor("attach-0")).FullName;
        var candidate   = Directory.CreateDirectory(f.Orch.AttachmentStore.DirectoryFor("attach-1")).FullName;
        var interrupted = Directory.CreateDirectory(f.Orch.AttachmentStore.DirectoryFor("attach-2")).FullName;
        var unknown     = Directory.CreateDirectory(f.Orch.AttachmentStore.DirectoryFor("unknown-1")).FullName;

        f.Orch.LoadResumeLedger();
        f.Orch.AttachmentStore.SweepOrphans(f.Orch.IsLiveAttachmentStem, NullLogger.Instance);

        await Assert.That(Directory.Exists(candidate)).IsTrue();
        await Assert.That(Directory.Exists(interrupted)).IsTrue();
        await Assert.That(Directory.Exists(inheritedDir)).IsTrue();
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
