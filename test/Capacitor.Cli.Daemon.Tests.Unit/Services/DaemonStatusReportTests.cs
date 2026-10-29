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

    /// <summary>A report built inside the registration bracket is dropped, so a count change there reaches
    /// the server only through the report sent once readiness returns.</summary>
    [Test]
    public async Task Readiness_returning_sends_a_status_report() {
        var server = new CaptureServerConnection();
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            server, new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        orch.SeedAgentForTest("a1", LaunchKind.Default, status: "Running");

        await server.OnRegisteredHook!.Invoke();

        await WaitHarness.PollUntilAsync(() => server.StatusReportCount > 0);
        await Assert.That(server.StatusReports[0].ActiveCount).IsEqualTo(1);
    }

    /// <summary>The server keeps the reading with the highest version, so every change of the count must
    /// advance it, whether the agent stops or leaves the registry.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Report_version_advances_with_every_count_change(bool unpublished) {
        await using var orch = AgentOrchestratorHarness.BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

        var before = orch.BuildStatusReport();
        var agent = orch.SeedAgentForTest("a1", LaunchKind.Default, status: "Running");
        var counting = orch.BuildStatusReport();
        if (unpublished) orch.UnpublishAgentForTest("a1");
        else orch.SetAgentStatus(agent, "Stopped");
        var stopped = orch.BuildStatusReport();

        await Assert.That(counting.ActiveCount).IsEqualTo(1);
        await Assert.That(counting.ActiveCountVersion!.Value).IsGreaterThan(before.ActiveCountVersion!.Value);
        await Assert.That(stopped.ActiveCount).IsEqualTo(0);
        await Assert.That(stopped.ActiveCountVersion!.Value).IsGreaterThan(counting.ActiveCountVersion!.Value);
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
