using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Tests.Unit.Daemon;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// Phase B (D4 §6.4(3)): <c>HandleStopAgent</c> for an id not in the registry falls back to the
/// PID record and reaps a matching live process by exact identity. OS-aware: Linux confirms the
/// <c>KCAP_AGENT_ID</c> env and reaps; macOS 26 can't read the env, so it SPARES (ambiguity never
/// kills). Partial of <see cref="AgentOrchestratorVendorTests"/> to call the private HandleStopAgent.
/// </summary>
public partial class AgentOrchestratorVendorTests {
    [Test]
    public async Task HandleStopAgent_unknown_id_reaps_by_pid_record_where_env_is_readable() {
        await using var orch = BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        using var dummy = DummyProcess.StartSleep(30, new Dictionary<string, string> { ["KCAP_AGENT_ID"] = "ghost" });
        var identity = ProcessIdentity.Capture(dummy.Pid);
        await Assert.That(identity).IsNotNull();

        orch.WritePidRecordForTest(new AgentPidRecord(
            "ghost", dummy.Pid, identity!, PidIdentityKind.Present, "ReviewFlow", "codex", "f1", "reviewer",
            orch.DaemonIdForTest, orch.DaemonEpochForTest, DateTimeOffset.UtcNow));

        await orch.HandleStopAgent("ghost");

        if (!OperatingSystem.IsMacOS()) {
            // Linux / Windows → ownership is provable (Linux confirms via the KCAP_AGENT_ID env,
            // Windows via an exact (pid, start-identity) match), so the record-pass reap kills by
            // identity and deletes the record on CONFIRMED death.
            dummy.WaitForExit(TimeSpan.FromSeconds(10));
            await Assert.That(dummy.HasExited).IsTrue();
            await Assert.That(orch.PidRecordsForTest().Any(r => r.AgentId == "ghost")).IsFalse();
        } else {
            // macOS → whether the reap can PROVE ownership depends on the OS version's start-identity
            // readability: older macOS redacts another process's identity (Ambiguous → spared, record
            // retained), macOS 26+ can read it (Ours → reaped by identity, deleted on confirmed death,
            // converging with Linux/Windows). Accept either outcome, but assert the real invariant —
            // the record is deleted IFF the process was confirmed gone. (This is what the fix restores:
            // the reaper must not report a still-alive-then-killed process as unconfirmed and strand its
            // record — deleted-vs-retained must track the process's actual fate.)
            dummy.WaitForExit(TimeSpan.FromSeconds(10));
            var retained = orch.PidRecordsForTest().Any(r => r.AgentId == "ghost");
            await Assert.That(retained).IsEqualTo(!dummy.HasExited);
            if (!dummy.HasExited) dummy.Kill();
        }
    }

    [Test]
    public async Task HandleStopAgent_unknown_id_with_no_record_is_a_noop() {
        await using var orch = BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        // No record, no in-memory agent → must not throw.
        await orch.HandleStopAgent("does-not-exist");
        await Assert.That(orch.PidRecordsForTest()).IsEmpty();
    }
}
