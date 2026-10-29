using System.Diagnostics;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Resume;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// A keep-agents shutdown stops interactive agents so a successor daemon can resume them: it reports
/// no ending for them and leaves their ledger entry, ring, worktree and PID record behind. An
/// end-agents shutdown ends every agent as it always has.
/// </summary>
public class AgentOrchestratorStopForResumeTests {
    const string InstanceId = "instance-predecessor";

    /// The harness deletes its own daemon store on dispose, so what the shutdown leaves behind is
    /// read from a store this test owns.
    [TempDaemonPaths] public required TempDaemonStore Store { get; init; }

    [Test]
    public async Task KeepAgents_marks_interactive_agents_resuming_and_reports_no_shutdown_failure() {
        var server = new CaptureServerConnection();
        var orch   = Build(server);
        Seed(orch, "keep-1");
        Seed(orch, "keep-2");

        await orch.DisposeAsync();

        await Assert.That(server.ShutdownReports).IsEmpty();

        foreach (var id in new[] { "keep-1", "keep-2" }) {
            var entry = Ledger().TryRead(id);
            await Assert.That(entry).IsNotNull();
            await Assert.That(entry!.State).IsEqualTo(ResumeState.Resuming);
            await Assert.That(entry.PredecessorInstanceId).IsEqualTo(InstanceId);
            await Assert.That(entry.ResumingAt).IsNotNull();
        }
    }

    /// <summary>Whether the agent was waiting at shutdown is what decides if its successor nudges it.</summary>
    [Test]
    public async Task KeepAgents_records_whether_each_agent_was_waiting_for_input() {
        var orch = Build(new CaptureServerConnection());
        Seed(orch, "idle").ActivityClock.SetAwaitingInput(true);
        Seed(orch, "working").ActivityClock.SetAwaitingInput(false);

        await orch.DisposeAsync();

        await Assert.That(Ledger().TryRead("idle")!.AwaitingInput).IsTrue();
        await Assert.That(Ledger().TryRead("working")!.AwaitingInput).IsFalse();
    }

    [Test]
    public async Task KeepAgents_persists_the_ring_and_keeps_the_worktree_and_pid_record() {
        using var tmp      = new TempDir();
        var       worktree = tmp.CreateDir("worktree");
        var       orch     = Build(new CaptureServerConnection());
        var       agent    = Seed(orch, "keep-ring", worktree);
        agent.OutputBuffer.Append("first "u8.ToArray());
        agent.OutputBuffer.Append("second"u8.ToArray());
        var snapshot = agent.OutputBuffer.Snapshot();
        orch.WritePidRecordForTest(new AgentPidRecord(
            "keep-ring", 999_999, "identity", PidIdentityKind.Present, "Default", "cursor", null, null,
            orch.DaemonIdForTest, orch.DaemonEpochForTest, TimeProvider.System.GetUtcNow()));

        await orch.DisposeAsync();

        var ringPath = Ledger().RingPath("keep-ring");
        await Assert.That(File.ReadAllBytes(ringPath)).IsEquivalentTo(snapshot);
        await Assert.That(Directory.Exists(worktree)).IsTrue();
        await Assert.That(new AgentPidRecordStore(Store.Store.StateDirectory("test"), NullLogger.Instance).Exists("keep-ring")).IsTrue();

        if (!OperatingSystem.IsWindows())
            await Assert.That(File.GetUnixFileMode(ringPath)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Test]
    public async Task KeepAgents_calls_graceful_stop_before_terminate() {
        var orch    = Build(new CaptureServerConnection());
        var runtime = new FakeHostedAgentRuntime("cursor", emitsTerminalOutput: true);
        Seed(orch, "keep-order", runtime: runtime);

        await orch.DisposeAsync();

        await Assert.That(string.Join(",", runtime.StopCalls)).IsEqualTo("graceful,terminate");
    }

    /// The stop must fit a supervisor's exit budget: a child that ignores the graceful request is
    /// terminated once the grace runs out, however long the request itself would block.
    [Test]
    public async Task KeepAgents_terminates_a_runtime_that_never_exits_once_the_grace_runs_out() {
        var orch    = Build(new CaptureServerConnection());
        var runtime = new FakeHostedAgentRuntime("cursor", emitsTerminalOutput: true) { NeverExitsOnItsOwn = true };
        Seed(orch, "keep-stuck", runtime: runtime);
        await Assert.That(orch.ShutdownStopGrace).IsEqualTo(TimeSpan.FromSeconds(5));

        var elapsed = Stopwatch.StartNew();
        await orch.DisposeAsync();
        elapsed.Stop();

        await Assert.That(string.Join(",", runtime.StopCalls)).IsEqualTo("graceful,terminate");
        await Assert.That(elapsed.Elapsed).IsLessThan(TimeSpan.FromSeconds(10));
        await Assert.That(Ledger().TryRead("keep-stuck")!.State).IsEqualTo(ResumeState.Resuming);
    }

    [Test]
    public async Task EndAgents_keeps_todays_shutdown_report() {
        var server = new CaptureServerConnection();
        var orch   = Build(server);
        orch.ShutdownIntent = ShutdownIntent.EndAgents;
        Seed(orch, "end-1");
        Seed(orch, "end-2");

        await orch.DisposeAsync();

        foreach (var id in new[] { "end-1", "end-2" }) {
            await Assert.That(server.ShutdownReports.Count(r => r.AgentId == id)).IsEqualTo(1);
            await Assert.That(server.ShutdownReports)
                .Contains((id, "Failed", AgentOrchestrator.DaemonShutdownStopReason, false));
            await Assert.That(Ledger().TryRead(id)).IsNull();
        }
    }

    [Test]
    public async Task A_resumable_reviewer_is_parked_not_prepared() {
        var server = new CaptureServerConnection { ParkOutcome = ParkAck.Parked };
        var orch   = Build(server);
        var agent  = SeedResumableReviewer(orch, "park-on-shutdown", "thread-shutdown");

        await orch.DisposeAsync();

        await Assert.That(server.ParkReports)
            .Contains(("park-on-shutdown", "thread-shutdown", AgentOrchestrator.ReviewerParkedResumableReason));
        await Assert.That(agent.ResumePending).IsFalse();
        await Assert.That(Ledger().TryRead("park-on-shutdown")).IsNull();
        await Assert.That(server.ShutdownReports.Any(r => r.AgentId == "park-on-shutdown")).IsFalse();
    }

    /// A park waiting on the interactive stops would report its ack only after their grace, late in
    /// the exit budget; it goes out while a stuck interactive agent is still in its grace.
    [Test]
    public async Task A_park_is_reported_while_interactive_agents_are_still_stopping() {
        var parkEntered = new TaskCompletionSource();
        var server      = new CaptureServerConnection { ParkOutcome = ParkAck.Parked, ParkEntered = parkEntered };
        var orch        = Build(server);
        var stuck       = new FakeHostedAgentRuntime("cursor", emitsTerminalOutput: true) { NeverExitsOnItsOwn = true };
        Seed(orch, "keep-slow", runtime: stuck);
        SeedResumableReviewer(orch, "park-early");
        string? interactiveAtPark = null;
        _ = parkEntered.Task.ContinueWith(
            _ => { lock (stuck.StopCalls) interactiveAtPark = string.Join(",", stuck.StopCalls); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        await orch.DisposeAsync();

        await Assert.That(server.ParkReports.Any(r => r.AgentId == "park-early")).IsTrue();
        await Assert.That(interactiveAtPark).IsNotNull();
        await Assert.That(interactiveAtPark!).DoesNotContain("terminate");
    }

    [Test]
    public async Task Ring_replay_caps_at_MaxBytes_and_tolerates_truncation() {
        await using var orch = Build(new CaptureServerConnection());

        var oversized = orch.SeedAgentForTest("replay-big");
        var ring      = new byte[TerminalOutputBuffer.MaxBytes + 1];
        ring[0] = (byte) 'X';
        Array.Fill(ring, (byte) 'a', 1, ring.Length - 1);
        AgentOrchestrator.ReplayRing(oversized, ring);

        var replayed = oversized.OutputBuffer.Snapshot();
        var marker   = AgentOrchestrator.RestartMarker;
        await Assert.That(replayed.Length).IsLessThanOrEqualTo(TerminalOutputBuffer.MaxBytes);
        await Assert.That(replayed.AsSpan(replayed.Length - marker.Length).SequenceEqual(marker)).IsTrue();
        await Assert.That(Array.IndexOf(replayed, (byte) 'X')).IsEqualTo(-1);
        await Assert.That(replayed.Length - marker.Length).IsGreaterThan(0);

        var empty = orch.SeedAgentForTest("replay-empty");
        AgentOrchestrator.ReplayRing(empty, []);

        await Assert.That(empty.OutputBuffer.Snapshot()).IsEquivalentTo(marker);
    }

    /// <summary>A run whose session end was already committed may have that end on its way to the
    /// server, which would stop whatever a successor resumed under the id.</summary>
    [Test]
    public async Task A_committed_end_keeps_the_agent_from_being_marked_resuming() {
        var orch  = Build(new CaptureServerConnection());
        var agent = Seed(orch, "ended-1");
        var entry = Ledger().TryRead("ended-1")!;
        Ledger().Write(entry with { EndCommittedAt = TimeProvider.System.GetUtcNow(), EndCommittedSession = entry.SessionId });

        await orch.PrepareForResumeAsync(agent, CancellationToken.None);

        await Assert.That(Ledger().TryRead("ended-1")!.State).IsEqualTo(ResumeState.Running);
        await Assert.That(agent.ResumePending).IsFalse();
        await orch.DisposeAsync();
    }

    /// <summary>An end committed for an earlier session of the same process, as a Claude /clear does,
    /// leaves the current session resumable.</summary>
    [Test]
    public async Task An_end_committed_for_an_earlier_session_does_not_block_the_resume() {
        var orch  = Build(new CaptureServerConnection());
        var agent = Seed(orch, "cleared-1");
        agent.SessionId = "session-now";
        orch.UpdateLedgerSession(agent);
        var entry = Ledger().TryRead("cleared-1")!;
        Ledger().Write(entry with { EndCommittedAt = TimeProvider.System.GetUtcNow(), EndCommittedSession = "session-before" });

        await orch.PrepareForResumeAsync(agent, CancellationToken.None);

        await Assert.That(Ledger().TryRead("cleared-1")!.State).IsEqualTo(ResumeState.Resuming);
        await orch.DisposeAsync();
    }

    [Test]
    public async Task A_launch_records_the_daemon_epoch_it_runs_under() {
        var orch = Build(new CaptureServerConnection());
        Seed(orch, "epoch-1");

        var entry = Ledger().TryRead("epoch-1")!;

        await Assert.That(entry.RunEpoch).IsEqualTo(orch.DaemonEpochForTest);
        await Assert.That(entry.LaunchEpoch).IsEqualTo(orch.DaemonEpochForTest);
        await orch.DisposeAsync();
    }

    AgentOrchestrator Build(CaptureServerConnection server) => AgentOrchestratorHarness.BuildOrchestrator(
        server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
        configure: c => {
            c.Store      = Store.Store;
            c.InstanceId = InstanceId;
        });

    ResumeLedgerStore Ledger() => ResumeLedgerStore.For(Store.Store, "test");

    static AgentInstance SeedResumableReviewer(AgentOrchestrator orch, string id, string threadId = "thread-1") {
        var agent = new AgentInstance(
            id, "review this", "default", null, "/repo", "codex",
            new AgentOrchestratorReviewerParkTests.FakeAppServerRuntime(threadId),
            new WorktreeInfo("/repo", "b", "/repo"),
            new CancellationTokenSource()) {
            CreatedAt     = TimeProvider.System.GetUtcNow().UtcDateTime,
            LastOutputAt  = TimeProvider.System.GetUtcNow().UtcDateTime,
            Kind          = LaunchKind.ReviewFlow,
            Status        = "Running",
            ActivityClock = new AgentActivityClock(TimeProvider.System),
            Work          = WorkLocation.BorrowedCwd
        };
        orch.RegisterAgentForTest(agent);

        return agent;
    }

    static AgentInstance Seed(
            AgentOrchestrator orch, string id, string? worktree = null, FakeHostedAgentRuntime? runtime = null) {
        var agent = AgentOrchestratorHarness.SeedAcpAgent(
            orch, id, runtime ?? new FakeHostedAgentRuntime("cursor", emitsTerminalOutput: true), worktreePath: worktree);
        orch.WriteLedger(agent, cmd: null, origin: "server", consent: null);

        return agent;
    }
}
