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

        // The record-pass reap kills once ownership is provable: Linux confirms via the KCAP_AGENT_ID
        // env, Windows has no Unix env guard so an exact (pid, start-identity) match is sufficient. Only
        // macOS 26 spares — its env is redacted, so the (Unix) env guard can't confirm and ambiguity
        // never kills.
        if (!OperatingSystem.IsMacOS()) {
            // Linux / Windows → reaped by identity, record deleted on confirmed death.
            dummy.WaitForExit(TimeSpan.FromSeconds(10));
            await Assert.That(dummy.HasExited).IsTrue();
            await Assert.That(orch.PidRecordsForTest().Any(r => r.AgentId == "ghost")).IsFalse();
        } else {
            // macOS: env unreadable → SPARED; process still alive, record retained for a later sweep.
            await Assert.That(dummy.HasExited).IsFalse();
            await Assert.That(orch.PidRecordsForTest().Any(r => r.AgentId == "ghost")).IsTrue();
            dummy.Kill();
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
