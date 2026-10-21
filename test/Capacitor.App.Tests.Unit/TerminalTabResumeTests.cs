using System.Reactive.Threading.Tasks;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Resume;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

/// How a terminal tab rides out a daemon restart: it waits while the resume ledger says the agent is
/// coming back, reattaches by id once it is, and says why when it is not.
public class TerminalTabResumeTests {
    static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    sealed class Rig {
        public readonly FakeDaemonClientService Daemon = new();
        public readonly FakeTerminalAttachClientFactory Factory = new();
        public readonly FakeTimeProvider Time = new();
        public ResumeLedgerEntry? Entry;
        public volatile Func<ResumeLedgerEntry?>? Gate;
        public TerminalTabViewModel Vm = null!;

        /// A daemon that reports which runtime an attach reached.
        public Rig ReportingRuntime() {
            Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, [AgentAttachClient.RuntimeCapability]));
            return this;
        }

        public async Task<FakeTerminalAttachClient> StartAsync() {
            Vm = new TerminalTabViewModel("a1", Daemon, Factory.Factory, () => new FakeTerminalSurface(), Time,
                agentId => agentId != "a1" ? null : Gate is { } gate ? gate() : Entry, Window);
            Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true));
            Dispatcher.UIThread.RunJobs();
            await (Vm.PendingResolveWorkForTesting ?? Task.CompletedTask);
            var client = Factory.Created.Single();
            await client.RunStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return client;
        }

        public async Task<FakeTerminalAttachClient> AttachAsync(DateTimeOffset? runtimeStartedAt = null) {
            var client = await StartAsync();
            await client.TriggerAttached("before"u8.ToArray(), runtimeStartedAt: runtimeStartedAt);
            return client;
        }

        public async Task RecoveryAsync() {
            await (Vm.PendingRecoveryForTesting ?? Task.CompletedTask);
            Dispatcher.UIThread.RunJobs();
        }

        public async Task DropAsync(FakeTerminalAttachClient client, AttachOutcome outcome) {
            client.Result.SetResult(outcome);
            await Vm.CurrentRunForTesting!;
        }

        public async Task TickAsync() {
            Time.Advance(TerminalTabViewModel.ResumeRetryGap);
            for (var i = 0; i < 5; i++) {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(5);
            }
        }
    }

    static readonly DateTimeOffset FirstStart = DateTimeOffset.UnixEpoch.AddHours(1);
    static readonly DateTimeOffset SecondStart = DateTimeOffset.UnixEpoch.AddHours(2);

    static ResumeLedgerEntry Ledger(ResumeState state, string? lastError = null, DateTimeOffset? startedAt = null) {
        var at = DateTimeOffset.UnixEpoch;
        var launch = new ResumeLaunchDescriptor(
            Prompt: null, Model: null, Effort: null, RepoPath: "/repo", WorktreePath: "/repo/wt", Work: WorkLocation.OwnedWorktree,
            GitRelativeCwd: null, SandboxPolicy: null, ApprovalPolicy: null, PermissionPreset: null, McpConfigPath: null,
            Title: null, IsPrivate: false, Origin: "local", RequesterUserId: null, RequesterDisplay: null,
            ConsentSource: null, ConsentAt: null, FlowRunId: null, FlowRole: null);
        return new ResumeLedgerEntry(
            ResumeLedgerEntry.CurrentSchemaVersion, "a1", "daemon", at, at, "claude", "pty", null, null, launch, state,
            Attempts: 0, LastError: lastError, PredecessorInstanceId: null, ResumingAt: null,
            StartedAt: startedAt ?? (state == ResumeState.Running ? FirstStart : null));
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_dropped_connection_waits_for_the_resume_then_reattaches_by_id_with_a_fresh_replay() {
        await RunOnUiAsync(async () => {
            var rig = new Rig { Entry = Ledger(ResumeState.Running) };
            var client = await rig.AttachAsync();
            var before = rig.Vm.Surface;

            rig.Entry = Ledger(ResumeState.Resuming);
            await rig.DropAsync(client, new AttachOutcome.ConnectionLost());

            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Resuming);
            await Assert.That(rig.Vm.SendAvailability).IsEqualTo(SendAvailability.Connecting);
            await rig.TickAsync();
            await Assert.That(rig.Factory.Created.Count).IsEqualTo(1);

            rig.Entry = Ledger(ResumeState.Running);
            await rig.TickAsync();

            var next = rig.Factory.Created[^1];
            await Assert.That(rig.Factory.Created.Count).IsEqualTo(2);
            await next.TriggerAttached("ring── kcap daemon restarted; session resumed ──"u8.ToArray());

            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Attached);
            await Assert.That(rig.Vm.Surface).IsNotSameReferenceAs(before);
            await Assert.That(((FakeTerminalSurface)rig.Vm.Surface!).Fed).Contains("ring── kcap daemon restarted; session resumed ──");
        });
    }

    /// The successor refuses an attach until the resume publishes the agent, so that refusal keeps waiting.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_refused_reattach_while_resuming_keeps_waiting() {
        await RunOnUiAsync(async () => {
            var rig = new Rig { Entry = Ledger(ResumeState.Resuming) };
            var client = await rig.AttachAsync();
            await rig.DropAsync(client, new AttachOutcome.ConnectionLost());
            rig.Entry = Ledger(ResumeState.Running);
            await rig.TickAsync();

            rig.Entry = Ledger(ResumeState.Resuming);
            await rig.DropAsync(rig.Factory.Created[^1], new AttachOutcome.Failed($"{LocalFrame.NoSuchAgentPrefix} a1"));

            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Resuming);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_agent_that_ends_up_interrupted_shows_the_reason() {
        await RunOnUiAsync(async () => {
            var rig = new Rig { Entry = Ledger(ResumeState.Resuming) };
            var client = await rig.AttachAsync();
            await rig.DropAsync(client, new AttachOutcome.ConnectionLost());

            rig.Entry = Ledger(ResumeState.Interrupted, "worktree_missing");
            await rig.TickAsync();

            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Interrupted);
            await Assert.That(rig.Vm.State.Detail).IsEqualTo("worktree_missing");
            await Assert.That(rig.Vm.SendAvailability).IsEqualTo(SendAvailability.Ended);
            await Assert.That(rig.Factory.Created.Count).IsEqualTo(1);
        });
    }

    /// Following the banner's advice brings the agent back under the same id, so the tab follows it.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_interrupted_tab_reattaches_once_a_retried_resume_brings_the_agent_back() {
        await RunOnUiAsync(async () => {
            var rig = new Rig { Entry = Ledger(ResumeState.Resuming) };
            var client = await rig.AttachAsync();
            await rig.DropAsync(client, new AttachOutcome.ConnectionLost());
            rig.Entry = Ledger(ResumeState.Interrupted, "runtime_exited");
            await rig.TickAsync();
            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Interrupted);

            rig.Entry = Ledger(ResumeState.Resuming);
            rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { Status = "Resuming" });
            await rig.RecoveryAsync();
            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Resuming);

            rig.Entry = Ledger(ResumeState.Running);
            await rig.TickAsync();
            await Assert.That(rig.Factory.Created.Count).IsEqualTo(2);
            await rig.Factory.Created[^1].TriggerAttached("back"u8.ToArray());
            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Attached);
        });
    }

    /// A resumed runtime that fails its readiness window exits under an attached tab before the daemon
    /// marks it interrupted, so the tab follows the later status rather than staying exited.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_exit_during_the_readiness_window_is_followed_to_interrupted_and_back() {
        await RunOnUiAsync(async () => {
            var rig = new Rig { Entry = Ledger(ResumeState.Running) };
            var client = await rig.AttachAsync();
            await rig.DropAsync(client, new AttachOutcome.Exited(3));
            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Exited);

            rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { Status = "Failed" });
            rig.Entry = Ledger(ResumeState.Interrupted, "readiness_failed");
            rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { Status = "Interrupted", Interruption = "readiness_failed" });
            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Interrupted);
            await Assert.That(rig.Vm.State.Detail).IsEqualTo("readiness_failed");

            rig.Entry = Ledger(ResumeState.Resuming);
            rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { Status = "Resuming" });
            await rig.RecoveryAsync();
            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Resuming);
        });
    }

    /// The row and the attach outcome arrive in either order; the interruption wins whichever is last.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(false)]
    [Arguments(true)]
    public async Task An_interrupted_row_that_arrives_before_the_exit_still_shows_the_reason(bool failedRowFirst) {
        await RunOnUiAsync(async () => {
            var rig = new Rig { Entry = Ledger(ResumeState.Running) };
            var client = await rig.AttachAsync();

            if (failedRowFirst) rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { Status = "Failed" });
            rig.Entry = Ledger(ResumeState.Interrupted, "readiness_failed");
            rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { Status = "Interrupted", Interruption = "readiness_failed" });
            await rig.DropAsync(client, new AttachOutcome.Exited(3));

            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Interrupted);
            await Assert.That(rig.Vm.State.Detail).IsEqualTo("readiness_failed");
        });
    }

    /// A manual retry can publish the replacement while the old stream is still draining, so the
    /// transitions the cache showed meanwhile still start the wait once the old attempt ends.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_retry_that_completes_while_the_old_attempt_drains_is_followed() {
        await RunOnUiAsync(async () => {
            var rig = new Rig { Entry = Ledger(ResumeState.Running) };
            var client = await rig.AttachAsync();

            rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { Status = "Interrupted" });
            rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { Status = "Resuming" });
            rig.Entry = Ledger(ResumeState.Running, startedAt: SecondStart);
            rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true));
            await rig.DropAsync(client, new AttachOutcome.Exited(1));
            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Resuming);

            await rig.TickAsync();
            await Assert.That(rig.Factory.Created.Count).IsEqualTo(2);
        });
    }

    /// A cache stuck on Resuming after the window ran out is the same row replayed, not a new retry.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_expired_window_is_not_renewed_by_an_unchanged_resuming_row() {
        await RunOnUiAsync(async () => {
            var rig = new Rig { Entry = Ledger(ResumeState.Running) };
            var client = await rig.AttachAsync();
            rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { Status = "Resuming" });
            await rig.DropAsync(client, new AttachOutcome.ConnectionLost());
            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Resuming);

            // The ledger says Running, but every attach fails until the window runs out.
            for (var waited = TimeSpan.Zero; waited <= Window + Window && rig.Vm.State.Phase != TerminalSessionPhase.Failed;
                 waited += TerminalTabViewModel.ResumeRetryGap) {
                await rig.TickAsync();
                if (rig.Factory.Created[^1].Result.Task.IsCompleted is false)
                    await rig.DropAsync(rig.Factory.Created[^1], new AttachOutcome.Failed("connect refused"));
            }
            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Failed);

            rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { Status = "Resuming", Title = "same status" });
            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Failed);
        });
    }

    /// A retry that lands while a poll is reading the ledger is newer than that poll's verdict.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_retry_seen_while_a_poll_settles_starts_another_wait() {
        await RunOnUiAsync(async () => {
            var rig = new Rig { Entry = Ledger(ResumeState.Resuming) };
            var client = await rig.AttachAsync();
            await rig.DropAsync(client, new AttachOutcome.ConnectionLost());

            using var inRead = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var gated = Ledger(ResumeState.Interrupted, "runtime_exited");
            rig.Gate = () => { inRead.Set(); release.Wait(TimeSpan.FromSeconds(5)); return gated; };
            rig.Time.Advance(TerminalTabViewModel.ResumeRetryGap);
            while (!inRead.IsSet) await Task.Delay(5);

            rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { Status = "Resuming" });
            rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true));
            rig.Gate = null;
            rig.Entry = Ledger(ResumeState.Running);
            var phases = new List<TerminalSessionPhase>();
            rig.Vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TerminalTabViewModel.State)) phases.Add(rig.Vm.State.Phase); };
            release.Set();
            // The re-arm to Resuming is a second dispatch queued off the UI thread after Interrupted publishes.
            bool Rearmed() => phases.Contains(TerminalSessionPhase.Interrupted) && rig.Vm.State.Phase == TerminalSessionPhase.Resuming;
            for (var i = 0; i < 200 && !Rearmed(); i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }

            await Assert.That(phases).Contains(TerminalSessionPhase.Interrupted);

            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Resuming);
            await rig.TickAsync();
            await Assert.That(rig.Factory.Created.Count).IsEqualTo(2);
        });
    }

    /// An attach acknowledged late belongs to the old runtime; it must not consume the retry seen meanwhile.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_late_acknowledgement_does_not_swallow_a_retry_seen_while_connecting() {
        await RunOnUiAsync(async () => {
            var rig = new Rig { Entry = Ledger(ResumeState.Running) };
            rig.Vm = new TerminalTabViewModel("a1", rig.Daemon, rig.Factory.Factory, () => new FakeTerminalSurface(), rig.Time,
                agentId => agentId == "a1" ? rig.Entry : null, Window);
            rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true));
            Dispatcher.UIThread.RunJobs();
            await (rig.Vm.PendingResolveWorkForTesting ?? Task.CompletedTask);
            var client = rig.Factory.Created.Single();
            await client.RunStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); // the attempt has read the first start

            rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { Status = "Interrupted" });
            rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { Status = "Resuming" });
            rig.Entry = Ledger(ResumeState.Running, startedAt: SecondStart);
            rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true));
            await client.TriggerAttached("old"u8.ToArray());
            await rig.DropAsync(client, new AttachOutcome.Exited(1));

            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Resuming);
        });
    }

    /// The resumed runtime's own start is not a later recovery: when it finishes normally the tab shows
    /// the exit, whichever side of the attach its Running row lands on.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task A_resumed_agent_that_finishes_normally_shows_its_exit(bool runningBeforeAttached, bool removedBeforeExit) {
        await RunOnUiAsync(async () => {
            var rig = new Rig { Entry = Ledger(ResumeState.Running) };
            var client = await rig.AttachAsync();
            rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { Status = "Resuming" });
            rig.Entry = Ledger(ResumeState.Resuming);
            await rig.DropAsync(client, new AttachOutcome.ConnectionLost());
            rig.Entry = Ledger(ResumeState.Running);
            await rig.TickAsync();
            var resumed = rig.Factory.Created[^1];

            if (runningBeforeAttached) rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true));
            await resumed.TriggerAttached("ring"u8.ToArray());
            if (!runningBeforeAttached) rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true));

            rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { Status = "Completed" });
            rig.Entry = null;
            if (removedBeforeExit) rig.Daemon.Agents.RemoveKey("a1");
            await rig.DropAsync(resumed, new AttachOutcome.Exited(0));
            await rig.TickAsync();

            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Exited);
            await Assert.That(rig.Factory.Created.Count).IsEqualTo(2);
        });
    }

    /// The status stream can lag the ledger: the restart's own Resuming row arriving after the replacement
    /// attached, and the replacement's exit arriving before its Completed row, still read as that
    /// runtime finishing.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_lagging_status_stream_leaves_a_resumed_agents_normal_exit_alone() {
        await RunOnUiAsync(async () => {
            var rig = new Rig { Entry = Ledger(ResumeState.Running) };
            var client = await rig.AttachAsync();
            rig.Entry = Ledger(ResumeState.Resuming);
            await rig.DropAsync(client, new AttachOutcome.ConnectionLost());
            rig.Entry = Ledger(ResumeState.Running, startedAt: SecondStart);
            await rig.TickAsync();
            var resumed = rig.Factory.Created[^1];
            await resumed.RunStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { Status = "Resuming" });
            await resumed.TriggerAttached("ring"u8.ToArray());
            rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true));
            await rig.DropAsync(resumed, new AttachOutcome.Exited(0));
            rig.Entry = null;
            rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { Status = "Completed" });
            await rig.TickAsync();
            await rig.TickAsync();

            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Exited);
            await Assert.That(rig.Factory.Created.Count).IsEqualTo(2);
        });
    }

    /// A restart can publish its successor between the ledger read and the daemon resolving the attach;
    /// the runtime the attach reached is the one the daemon reports, so its normal exit stays an exit.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_attach_is_bound_to_the_runtime_the_daemon_reports() {
        await RunOnUiAsync(async () => {
            var rig = new Rig { Entry = Ledger(ResumeState.Running) }.ReportingRuntime();
            var client = await rig.StartAsync();

            rig.Entry = Ledger(ResumeState.Running, startedAt: SecondStart);
            await client.TriggerAttached("successor"u8.ToArray(), runtimeStartedAt: SecondStart);
            await rig.DropAsync(client, new AttachOutcome.Exited(0));
            await rig.TickAsync();

            await Assert.That(client.RequestedRuntime).IsTrue();
            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Exited);
            await Assert.That(rig.Factory.Created.Count).IsEqualTo(1);
        });
    }

    /// A daemon without attach-runtime/1 gets a plain Attach, and the tab binds the runtime the ledger
    /// named before dialing.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_older_daemon_gets_a_plain_attach_bound_by_the_ledger_read_before_dialing() {
        await RunOnUiAsync(async () => {
            var rig = new Rig { Entry = Ledger(ResumeState.Running) };
            var client = await rig.StartAsync();

            rig.Entry = Ledger(ResumeState.Running, startedAt: SecondStart);
            await client.TriggerAttached("first"u8.ToArray());
            await rig.DropAsync(client, new AttachOutcome.Exited(1));

            await Assert.That(client.RequestedRuntime).IsFalse();
            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Resuming);
        });
    }

    /// The restart's own Resuming row can arrive after its replacement attached and exited normally; it
    /// names no runtime other than the one the tab ended on, so the exit stands.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_late_resuming_row_after_a_settled_exit_leaves_the_exit_alone(bool entryStillListed) {
        await RunOnUiAsync(async () => {
            var rig = new Rig { Entry = Ledger(ResumeState.Running) }.ReportingRuntime();
            var client = await rig.AttachAsync(FirstStart);
            rig.Entry = Ledger(ResumeState.Resuming);
            await rig.DropAsync(client, new AttachOutcome.ConnectionLost());
            rig.Entry = Ledger(ResumeState.Running, startedAt: SecondStart);
            await rig.TickAsync();
            var resumed = rig.Factory.Created[^1];
            await resumed.TriggerAttached("ring"u8.ToArray(), runtimeStartedAt: SecondStart);
            await rig.DropAsync(resumed, new AttachOutcome.Exited(0));
            if (!entryStillListed) rig.Entry = null;
            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Exited);

            rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { Status = "Resuming" });
            await rig.RecoveryAsync();
            await rig.TickAsync();
            await rig.TickAsync();

            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Exited);
            await Assert.That(rig.Factory.Created.Count).IsEqualTo(2);
        });
    }

    /// A manual retry that writes the ledger after the attempt read Interrupted reaches a tab still
    /// attached, where its rows change nothing; settling reads the ledger again and follows the retry.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_retry_seen_while_an_interrupted_verdict_settles_starts_a_wait() {
        await RunOnUiAsync(async () => {
            var rig = new Rig { Entry = Ledger(ResumeState.Running) }.ReportingRuntime();
            var client = await rig.AttachAsync(FirstStart);

            using var inRead = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var gated = Ledger(ResumeState.Interrupted, "runtime_exited");
            rig.Gate = () => { inRead.Set(); release.Wait(TimeSpan.FromSeconds(5)); return gated; };
            client.Result.SetResult(new AttachOutcome.ConnectionLost());
            while (!inRead.IsSet) await Task.Delay(5);

            rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { Status = "Interrupted" });
            rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { Status = "Resuming" });
            rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true));
            rig.Gate = null;
            rig.Entry = Ledger(ResumeState.Running, startedAt: SecondStart);
            var phases = new List<TerminalSessionPhase>();
            rig.Vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(TerminalTabViewModel.State)) phases.Add(rig.Vm.State.Phase); };
            release.Set();
            await rig.Vm.CurrentRunForTesting!;

            await Assert.That(phases).Contains(TerminalSessionPhase.Interrupted);
            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Resuming);
            await rig.TickAsync();
            await Assert.That(rig.Factory.Created.Count).IsEqualTo(2);
        });
    }

    /// An Interrupted row that reaches a still-attached tab between its own runtime's exit verdict and
    /// that verdict's publication is not overwritten by the exit.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_interruption_seen_while_an_own_exit_settles_shows_the_reason() {
        await RunOnUiAsync(async () => {
            var rig = new Rig { Entry = Ledger(ResumeState.Running) }.ReportingRuntime();
            var client = await rig.AttachAsync(FirstStart);

            using var inRead = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var gated = Ledger(ResumeState.Running);
            rig.Gate = () => { inRead.Set(); release.Wait(TimeSpan.FromSeconds(5)); return gated; };
            client.Result.SetResult(new AttachOutcome.Exited(3));
            while (!inRead.IsSet) await Task.Delay(5);

            rig.Entry = Ledger(ResumeState.Interrupted, "readiness_failed");
            rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { Status = "Failed" });
            rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { Status = "Interrupted", Interruption = "readiness_failed" });
            rig.Gate = null;
            release.Set();
            await rig.Vm.CurrentRunForTesting!;

            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Interrupted);
            await Assert.That(rig.Vm.State.Detail).IsEqualTo("readiness_failed");
        });
    }

    /// A restart reconnects the status stream through states with no capabilities, and the daemon it
    /// reaches may predate the request, so only the daemon connected now decides how to attach.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(false)]
    [Arguments(true)]
    public async Task An_attach_asks_for_the_runtime_only_while_the_connected_daemon_offers_it(bool reconnectedToOlder) {
        await RunOnUiAsync(async () => {
            var rig = new Rig { Entry = Ledger(ResumeState.Running) }.ReportingRuntime();
            var client = await rig.AttachAsync(FirstStart);
            await Assert.That(client.RequestedRuntime).IsTrue();

            rig.Entry = Ledger(ResumeState.Resuming);
            rig.Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connecting, null, null));
            if (reconnectedToOlder)
                rig.Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["status/1", "resume/1"]));
            await rig.DropAsync(client, new AttachOutcome.ConnectionLost());
            rig.Entry = Ledger(ResumeState.Running, startedAt: SecondStart);
            await rig.TickAsync();

            await Assert.That(rig.Factory.Created.Count).IsEqualTo(2);
            await Assert.That(rig.Factory.Created[^1].RequestedRuntime).IsFalse();
        });
    }

    /// A retry whose rows land while settlement is publishing the interruption it read is still followed.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_retry_seen_while_settlement_publishes_an_interruption_starts_a_wait() {
        await RunOnUiAsync(async () => {
            var rig = new Rig { Entry = Ledger(ResumeState.Running) }.ReportingRuntime();
            var client = await rig.AttachAsync(FirstStart);

            using var recheckRead = new ManualResetEventSlim();
            using var releaseRecheck = new ManualResetEventSlim();
            using var recoveryRead = new ManualResetEventSlim();
            using var releaseRecovery = new ManualResetEventSlim();
            var reads = 0;
            var interrupted = Ledger(ResumeState.Interrupted, "readiness_failed");
            var stale = Ledger(ResumeState.Running, startedAt: SecondStart);
            rig.Gate = () => {
                switch (Interlocked.Increment(ref reads)) {
                    case 2: recheckRead.Set(); releaseRecheck.Wait(TimeSpan.FromSeconds(5)); return interrupted;
                    case 3: recoveryRead.Set(); releaseRecovery.Wait(TimeSpan.FromSeconds(5)); return stale;
                    default: return rig.Entry;
                }
            };
            client.Result.SetResult(new AttachOutcome.Exited(3));
            while (!recheckRead.IsSet) { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); }
            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Exited);

            rig.Entry = Ledger(ResumeState.Running, startedAt: SecondStart);
            rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { Status = "Resuming" });
            rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true));
            var recovery = rig.Vm.PendingRecoveryForTesting!;
            while (!recoveryRead.IsSet) await Task.Delay(5);

            releaseRecheck.Set();
            await rig.Vm.CurrentRunForTesting!;
            releaseRecovery.Set();
            await recovery;
            Dispatcher.UIThread.RunJobs();

            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Resuming);
            await rig.TickAsync();
            await Assert.That(rig.Factory.Created.Count).IsEqualTo(2);
        });
    }

    /// A status-driven read that outlives the attempt it started from cannot restart the wait for a later
    /// runtime whose exit happens to settle on an equal state.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_stale_status_recovery_leaves_a_later_runtimes_exit_alone() {
        await RunOnUiAsync(async () => {
            var rig = new Rig { Entry = Ledger(ResumeState.Running) }.ReportingRuntime();
            var client = await rig.AttachAsync(FirstStart);
            await rig.DropAsync(client, new AttachOutcome.Exited(0));
            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Exited);

            using var inRead = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var captured = Ledger(ResumeState.Resuming);
            rig.Gate = () => { inRead.Set(); release.Wait(TimeSpan.FromSeconds(5)); return captured; };
            rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { Status = "Resuming" });
            var stale = rig.Vm.PendingRecoveryForTesting!;
            while (!inRead.IsSet) await Task.Delay(5);

            rig.Gate = null;
            rig.Entry = Ledger(ResumeState.Resuming);
            rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { Status = "Completed" });
            rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { Status = "Resuming" });
            await rig.RecoveryAsync();
            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Resuming);

            rig.Entry = Ledger(ResumeState.Running, startedAt: SecondStart);
            await rig.TickAsync();
            var resumed = rig.Factory.Created[^1];
            await resumed.TriggerAttached("ring"u8.ToArray(), runtimeStartedAt: SecondStart);
            await rig.DropAsync(resumed, new AttachOutcome.Exited(0));
            rig.Entry = null;

            release.Set();
            await stale;
            await rig.TickAsync();
            await rig.TickAsync();

            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Exited);
            await Assert.That(rig.Factory.Created.Count).IsEqualTo(2);
        });
    }

    /// A daemon handing its agents to a successor marks them Resuming with the departing runtime's own
    /// stamp, so that runtime's exit is the restart and the tab waits for the resume.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_exit_while_the_attached_runtime_is_handed_over_waits_for_the_resume() {
        await RunOnUiAsync(async () => {
            var rig = new Rig { Entry = Ledger(ResumeState.Running) }.ReportingRuntime();
            var client = await rig.AttachAsync(FirstStart);

            rig.Entry = Ledger(ResumeState.Resuming, startedAt: FirstStart);
            await rig.DropAsync(client, new AttachOutcome.Exited(0));

            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Resuming);
        });
    }

    /// A reattach the user started while a status-driven ledger read was in flight keeps its attempt.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_pending_status_recovery_does_not_cancel_a_reattach_already_under_way() {
        await RunOnUiAsync(async () => {
            var rig = new Rig { Entry = Ledger(ResumeState.Running) }.ReportingRuntime();
            var client = await rig.AttachAsync(FirstStart);
            await rig.DropAsync(client, new AttachOutcome.Failed("boom"));
            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Failed);

            using var inRead = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var resuming = Ledger(ResumeState.Resuming);
            rig.Gate = () => { inRead.Set(); release.Wait(TimeSpan.FromSeconds(5)); return resuming; };
            rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { Status = "Resuming" });
            var recovery = rig.Vm.PendingRecoveryForTesting!;
            while (!inRead.IsSet) await Task.Delay(5);
            rig.Gate = null;

            client.DisposeGate = new TaskCompletionSource();
            var reattach = rig.Vm.ReattachCommand.Execute().ToTask();
            release.Set();
            await recovery;
            Dispatcher.UIThread.RunJobs();
            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Failed);

            client.DisposeGate.SetResult();
            await reattach;
            Dispatcher.UIThread.RunJobs();

            await Assert.That(rig.Factory.Created.Count).IsEqualTo(2);
            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Connecting);
        });
    }

    /// A dial that fails outright while the ledger says the agent is resuming is the restart, not a fault.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_failed_dial_while_resuming_waits_for_the_resume() {
        await RunOnUiAsync(async () => {
            var rig = new Rig { Entry = Ledger(ResumeState.Resuming) };
            var client = await rig.AttachAsync();

            await rig.DropAsync(client, new AttachOutcome.Failed("Connection refused"));

            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Resuming);
        });
    }

    /// The window bounds the reattach too: a resume that publishes after it has run out is not chased.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_resume_that_publishes_after_the_window_is_not_reattached() {
        await RunOnUiAsync(async () => {
            var rig = new Rig { Entry = Ledger(ResumeState.Resuming) };
            var client = await rig.AttachAsync();
            var deadline = rig.Time.GetUtcNow() + Window;
            rig.Gate = () => rig.Time.GetUtcNow() >= deadline ? Ledger(ResumeState.Running, startedAt: SecondStart) : Ledger(ResumeState.Resuming);
            await rig.DropAsync(client, new AttachOutcome.ConnectionLost());

            for (var waited = TimeSpan.Zero; waited <= Window; waited += TerminalTabViewModel.ResumeRetryGap) await rig.TickAsync();

            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Failed);
            await Assert.That(rig.Factory.Created.Count).IsEqualTo(1);
        });
    }

    /// An agent that simply finished keeps its exit, whatever stale status the cache still shows.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_ordinary_exit_stays_exited() {
        await RunOnUiAsync(async () => {
            var rig = new Rig();
            var client = await rig.AttachAsync();
            await rig.DropAsync(client, new AttachOutcome.Exited(0));

            rig.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with { Title = "still running in the cache" });

            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Exited);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_resume_that_outlasts_the_window_fails_as_a_lost_connection() {
        await RunOnUiAsync(async () => {
            var rig = new Rig { Entry = Ledger(ResumeState.Resuming) };
            var client = await rig.AttachAsync();
            await rig.DropAsync(client, new AttachOutcome.ConnectionLost());

            for (var waited = TimeSpan.Zero; waited <= Window; waited += TerminalTabViewModel.ResumeRetryGap) await rig.TickAsync();

            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Failed);
            await Assert.That(rig.Vm.State.Detail).Contains("lost connection");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_drop_with_no_ledger_entry_fails_at_once() {
        await RunOnUiAsync(async () => {
            var rig = new Rig();
            var client = await rig.AttachAsync();

            await rig.DropAsync(client, new AttachOutcome.ConnectionLost());

            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Failed);
            await Assert.That(rig.Vm.State.Detail).Contains("lost connection");
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_entry_that_disappears_mid_wait_says_the_agent_was_not_resumed() {
        await RunOnUiAsync(async () => {
            var rig = new Rig { Entry = Ledger(ResumeState.Resuming) };
            var client = await rig.AttachAsync();
            await rig.DropAsync(client, new AttachOutcome.ConnectionLost());

            rig.Entry = null;
            await rig.TickAsync();

            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Failed);
            await Assert.That(rig.Vm.State.Detail).Contains("kcap agent ls");
        });
    }

    [Test]
    public async Task The_interrupted_banner_names_the_reason_and_the_retry() {
        var withReason = Views.TerminalInterruptedMessageConverter.Instance.Convert(
            TerminalSessionState.Interrupted("too_old"), typeof(string), null, System.Globalization.CultureInfo.InvariantCulture);
        var without = Views.TerminalInterruptedMessageConverter.Instance.Convert(
            TerminalSessionState.Interrupted(null), typeof(string), null, System.Globalization.CultureInfo.InvariantCulture);

        await Assert.That(withReason).IsEqualTo("This session was not resumed after the daemon restarted (too_old). Retry it with `kcap agent resume`, or stop it to discard it.");
        await Assert.That(without).IsEqualTo("This session was not resumed after the daemon restarted. Retry it with `kcap agent resume`, or stop it to discard it.");
    }
}
