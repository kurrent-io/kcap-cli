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

    /// <summary>A launch keeps the daemon busy whether it is still pending, published while still pending, or published
    /// alone, so an uninstall's idle check never sees a running launch as nothing.</summary>
    [Test]
    public async Task The_busy_count_holds_a_launch_through_its_publish() {
        await using var orch = Build();
        var clock = new AgentActivityClock(TimeProvider.System);

        await Assert.That(orch.BusyForStatus()).IsFalse();

        var pending = orch.TrackPendingLaunch("p1", LaunchKind.Default, null, null, clock);
        await Assert.That(orch.BusyForStatus()).IsTrue();

        orch.SeedAgentForTest("p1", status: "Starting", activityClock: clock);
        await Assert.That(orch.BusyForStatus()).IsTrue();

        pending.Dispose();
        await Assert.That(orch.BusyForStatus()).IsTrue();
    }

    [Test]
    public async Task The_busy_count_holds_a_launch_that_overlaps_another_ending() {
        await using var orch = Build();
        var running = orch.SeedAgentForTest("a", status: "Running");
        using var pending = orch.TrackPendingLaunch("b", LaunchKind.Default, null, null, new AgentActivityClock(TimeProvider.System));

        orch.SetAgentStatus(running, "Exited");

        await Assert.That(orch.BusyForStatus()).IsTrue();
    }

    /// <summary>Consent, worktree and attachments run before a launch is pending; it is admitted throughout.</summary>
    [Test]
    public async Task The_busy_count_holds_an_admitted_launch_before_it_is_pending() {
        await using var orch = Build();

        using (orch.Admission.TryAdmit())
            await Assert.That(orch.BusyForStatus()).IsTrue();

        await Assert.That(orch.BusyForStatus()).IsFalse();
    }
}
