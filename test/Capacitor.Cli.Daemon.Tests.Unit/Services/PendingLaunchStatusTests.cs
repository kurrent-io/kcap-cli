using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Daemon.Tests.Unit.Pty;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// The local status snapshot describes a launch whose runtime is still starting, so a client can
/// show the agent before the daemon publishes it.
public class PendingLaunchStatusTests {
    static AgentOrchestrator Build() => AgentOrchestratorHarness.BuildOrchestrator(
        new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());

    [Test]
    public async Task A_pending_launch_rides_the_local_snapshot_with_its_identity_and_stage() {
        await using var orch = Build();
        var clock = new AgentActivityClock(TimeProvider.System);
        using var pending = orch.TrackPendingLaunch("p1", LaunchKind.Default, null, null, clock,
            vendor: "claude", repoPath: "/repo", title: "Fix the flaky test");

        var before = orch.SnapshotPendingForStatus().Single();
        await Assert.That(before.Id).IsEqualTo("p1");
        await Assert.That(before.Vendor).IsEqualTo("claude");
        await Assert.That(before.RepoPath).IsEqualTo("/repo");
        await Assert.That(before.Title).IsEqualTo("Fix the flaky test");
        await Assert.That(before.Stage).IsNull();

        clock.SetLaunchStage("spawned");
        await Assert.That(orch.SnapshotPendingForStatus().Single().Stage).IsEqualTo("spawned");
    }

    [Test]
    public async Task Pending_launches_list_in_creation_order() {
        await using var orch = Build();
        var clock = new AgentActivityClock(TimeProvider.System);
        using var second = orch.TrackPendingLaunch("b", LaunchKind.Default, null, null, clock, createdAt: new DateTime(2026, 9, 1, 0, 0, 1, DateTimeKind.Utc));
        using var first = orch.TrackPendingLaunch("a", LaunchKind.Default, null, null, clock, createdAt: new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc));

        await Assert.That(orch.SnapshotPendingForStatus().Select(p => p.Id))
            .IsEquivalentTo(new[] { "a", "b" }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_published_agent_leaves_the_pending_list() {
        await using var orch = Build();
        var clock = new AgentActivityClock(TimeProvider.System);
        using var pending = orch.TrackPendingLaunch("p1", LaunchKind.Default, null, null, clock);

        orch.SeedAgentForTest("p1", activityClock: clock);

        await Assert.That(orch.SnapshotPendingForStatus()).IsEmpty();
    }

    [Test]
    public async Task Tracking_and_a_stage_change_each_pulse_the_local_status_notifier() {
        await using var orch = Build();
        var clock = new AgentActivityClock(TimeProvider.System);
        var v0 = orch.StatusNotifierForTest.Version;

        using var pending = orch.TrackPendingLaunch("p1", LaunchKind.Default, null, null, clock);
        var v1 = orch.StatusNotifierForTest.Version;
        await Assert.That(v1).IsGreaterThan(v0);

        pending.Dispose();
        await Assert.That(orch.StatusNotifierForTest.Version).IsGreaterThan(v1);
    }

    /// The clock a real launch owns is built by the orchestrator, so its stage callback is the one
    /// under test here, not a callback the test wired itself.
    [Test]
    public async Task A_launch_stage_stamped_on_an_orchestrator_built_clock_pulses_the_local_notifier() {
        await using var orch = Build();
        var agent = orch.SeedAgentForTest("a1", status: "Starting");
        var v0 = orch.StatusNotifierForTest.Version;

        agent.ActivityClock.SetLaunchStage("initialized");

        await Assert.That(orch.StatusNotifierForTest.Version).IsGreaterThan(v0);
    }
}
