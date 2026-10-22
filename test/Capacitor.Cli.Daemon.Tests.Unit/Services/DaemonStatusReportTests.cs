using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// Phase B (D2): <see cref="AgentOrchestrator.BuildStatusReport"/> reports the daemon's
/// authoritative ActiveCount + live-agent metadata (quarantine wired in D4/Task 8).
/// Partial of <see cref="AgentOrchestratorVendorTests"/> to reuse its test doubles.
/// </summary>
public class DaemonStatusReportTests {
    [Test]
    public async Task BuildStatusReport_reports_active_count_and_live_agents() {
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        orch.SeedAgentForTest("a1", LaunchKind.ReviewFlow, status: "Running", flowRunId: "f1", flowRole: "reviewer");
        orch.SeedAgentForTest("a2", LaunchKind.Default,    status: "Running");
        orch.SeedAgentForTest("a3", LaunchKind.Default,    status: "Stopped");

        var report = orch.BuildStatusReport();

        await Assert.That(report.ActiveCount).IsEqualTo(2); // a1 + a2 (a3 stopped)
        await Assert.That(report.LiveAgents.Select(x => x.Id)).IsEquivalentTo(new[] { "a1", "a2" });
        await Assert.That(report.Quarantined).IsEmpty(); // until D4/Task 8
    }

    /// <summary>The server orders a register or unregister against a report by these versions: a report
    /// counting an agent carries a version at or past the one it began counting at, and a report no
    /// longer counting it one at or past the one it stopped at.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Report_version_orders_against_when_an_agent_starts_and_stops_counting(bool unpublished) {
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var before = orch.BuildStatusReport();
        var agent = orch.SeedAgentForTest("a1", LaunchKind.Default, status: "Running");
        var started = agent.ActiveCountVersion;
        var counting = orch.BuildStatusReport();
        if (unpublished) orch.UnpublishAgentForTest("a1");
        else orch.SetAgentStatus(agent, "Stopped");
        var stopped = orch.BuildStatusReport();

        await Assert.That(started).IsGreaterThan(before.ActiveCountVersion!.Value);
        await Assert.That((counting.ActiveCount, counting.ActiveCountVersion)).IsEqualTo((1, started));
        await Assert.That(agent.ActiveCountVersion).IsGreaterThan(started);
        await Assert.That((stopped.ActiveCount, stopped.ActiveCountVersion)).IsEqualTo((0, agent.ActiveCountVersion));
    }

    // Surface 3: a sent status report carries this machine's harness inventory (every vendor +
    // machine id). BuildStatusReport itself only reads the cache; the send path refreshes it first.
    [Test]
    public async Task Sent_status_report_carries_harness_inventory() {
        var capture = new CaptureServerConnection();
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            capture, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        await orch.SendDaemonStatusReportOnceAsync();

        var report = capture.StatusReports[^1];
        await Assert.That(report.HarnessInventory).IsNotNull();
        await Assert.That(report.HarnessInventory!.Vendors.Count).IsEqualTo(HarnessRegistry.Identities.Count);
        await Assert.That(string.IsNullOrEmpty(report.HarnessInventory!.MachineId)).IsFalse();
    }
}
