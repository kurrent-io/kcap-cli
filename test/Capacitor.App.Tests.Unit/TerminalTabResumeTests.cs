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
        public TerminalTabViewModel Vm = null!;

        public async Task<FakeTerminalAttachClient> AttachAsync() {
            Vm = new TerminalTabViewModel("a1", Daemon, Factory.Factory, () => new FakeTerminalSurface(), Time,
                agentId => agentId == "a1" ? Entry : null, Window);
            Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true));
            Dispatcher.UIThread.RunJobs();
            await (Vm.PendingResolveWorkForTesting ?? Task.CompletedTask);
            var client = Factory.Created.Single();
            await client.TriggerAttached("before"u8.ToArray());
            return client;
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

    static ResumeLedgerEntry Ledger(ResumeState state, string? lastError = null) {
        var at = DateTimeOffset.UnixEpoch;
        var launch = new ResumeLaunchDescriptor(
            Prompt: null, Model: null, Effort: null, RepoPath: "/repo", WorktreePath: "/repo/wt", Work: WorkLocation.OwnedWorktree,
            GitRelativeCwd: null, SandboxPolicy: null, ApprovalPolicy: null, PermissionPreset: null, McpConfigPath: null,
            Title: null, IsPrivate: false, Origin: "local", RequesterUserId: null, RequesterDisplay: null,
            ConsentSource: null, ConsentAt: null, FlowRunId: null, FlowRole: null);
        return new ResumeLedgerEntry(
            ResumeLedgerEntry.CurrentSchemaVersion, "a1", "daemon", at, at, "claude", "pty", null, null, launch, state,
            Attempts: 0, LastError: lastError, PredecessorInstanceId: null, ResumingAt: null);
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
            await Assert.That(rig.Vm.State.Phase).IsEqualTo(TerminalSessionPhase.Resuming);
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
