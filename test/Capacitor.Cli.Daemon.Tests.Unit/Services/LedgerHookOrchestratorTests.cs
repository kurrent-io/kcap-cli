using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Resume;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

// Phase B2-b (sequenced-settlement design §4.2.4): orchestrator-level proof that the quarantine-drain
// hook emits positive per-id death evidence into the real ResolvedCandidatesLedger owned by the
// orchestrator (append BEFORE the durable PID-record delete). Reuses the vendor test class's
// BuildOrchestrator/CaptureServerConnection/SpyPtyProcessFactory harness + the shared test seams.
public class LedgerHookOrchestratorTests {
    [Test]
    public async Task Quarantine_drain_via_orchestrator_emits_resolved_evidence_before_deleting_the_record() {
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>());

        using var dummy = DummyProcess.StartSleep(30);
        var pid      = dummy.Pid;
        var identity = ProcessIdentity.Capture(pid)!;
        dummy.Kill(); dummy.WaitForExit(TimeSpan.FromSeconds(5)); // confirmed dead -> the drain confirms + emits

        // Teardown retains the PID record (current epoch) of a quarantined survivor, so the drain deletes
        // it. Seed both the record and the quarantine entry to mirror that post-teardown state exactly.
        orch.WritePidRecordForTest(new AgentPidRecord(
            "q1", pid, identity, PidIdentityKind.Present, "ReviewFlow", "codex",
            "flow-1", "reviewer", orch.DaemonIdForTest, orch.DaemonEpochForTest, DateTimeOffset.UtcNow));
        orch.QuarantineForTest(new AgentKillQuarantine.Entry(
            "q1", pid, identity, "ReviewFlow", DateTimeOffset.UtcNow, "flow-1", "reviewer"));

        await orch.RetryQuarantineForTest();

        var entry = orch.ResolvedLedgerSnapshotForTest.Single();
        await Assert.That(entry.AgentId).IsEqualTo("q1");
        await Assert.That(entry.OldEpoch).IsEqualTo(orch.DaemonEpochForTest); // drain emits the current epoch
        await Assert.That(entry.FlowRole).IsEqualTo("reviewer");
        await Assert.That(orch.PidRecordsForTest()).IsEmpty(); // record deleted after the append
    }

    /// <summary>The server clears a flow role on evidence naming its run and role, and a participant
    /// stopped for resume comes back under the same id: its predecessor's death must not name them.</summary>
    [Test]
    [Arguments(ResumeState.Resuming, null)]
    [Arguments(ResumeState.Running, "reviewer")]
    public async Task A_reaped_predecessor_names_its_flow_role_only_when_the_participant_is_not_resuming(ResumeState state, string? expectedRole) {
        using var worktree = new TempDir();
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(),
            new Dictionary<string, IHostedAgentLauncher>(),
            configure: c => c.PriorInstanceId = "instance-old");

        using var dummy = DummyProcess.StartSleep(30);
        var pid      = dummy.Pid;
        var identity = ProcessIdentity.Capture(pid)!;
        dummy.Kill(); dummy.WaitForExit(TimeSpan.FromSeconds(5));

        orch.WritePidRecordForTest(new AgentPidRecord(
            "p1", pid, identity, PidIdentityKind.Present, "ReviewFlow", "claude",
            "flow-1", "reviewer", orch.DaemonIdForTest, "prior-epoch", DateTimeOffset.UtcNow));
        var planned = ResumeLedgerEntries.For("p1", state);
        orch.ParticipantLedger!.Write(planned with {
            ResumingAt = DateTimeOffset.UtcNow, PredecessorInstanceId = "instance-old", VendorSession = "session-1",
            Launch     = planned.Launch with { WorktreePath = worktree.Path }
        });

        await orch.ReapOrphansOnceAsync();

        var entry = orch.ResolvedLedgerSnapshotForTest.Single();
        await Assert.That(entry.OldEpoch).IsEqualTo("prior-epoch");
        await Assert.That(entry.FlowRole).IsEqualTo(expectedRole);
        await Assert.That(entry.FlowRunId).IsEqualTo(expectedRole is null ? null : "flow-1");
    }
}
