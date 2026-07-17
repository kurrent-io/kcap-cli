using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// AI-1313 Phase B (D3): the reviewer lifetime/idle backstop — <see cref="AgentOrchestrator.FindReviewersToReap"/>
/// reaps a Running ReviewFlow agent past its lifetime/idle bound and never an interactive agent.
/// Partial of <see cref="AgentOrchestratorVendorTests"/> to reuse its test doubles.
/// </summary>
public partial class AgentOrchestratorVendorTests {
    [Test]
    public async Task FindReviewersToReap_flags_lifetime_and_idle_but_not_interactive() {
        var now = new DateTime(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc);
        await using var orch = BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>());
        orch.ClockUtc = () => now; // defaults: 6h lifetime / 2h idle

        // 7h-old reviewer → past the 6h lifetime.
        orch.SeedAgentForTest("rev-old", LaunchKind.ReviewFlow, status: "Running",
            createdAt: now.AddHours(-7), lastOutputAt: now.AddMinutes(-1));
        // fresh reviewer, but idle 3h → past the 2h idle bound.
        orch.SeedAgentForTest("rev-idle", LaunchKind.ReviewFlow, status: "Running",
            createdAt: now.AddHours(-1), lastOutputAt: now.AddHours(-3));
        // interactive agent of the same age → never reaped by this backstop.
        orch.SeedAgentForTest("interactive", LaunchKind.Default, status: "Running",
            createdAt: now.AddHours(-7), lastOutputAt: now.AddHours(-7));
        // healthy reviewer → left alone.
        orch.SeedAgentForTest("rev-fresh", LaunchKind.ReviewFlow, status: "Running",
            createdAt: now.AddMinutes(-10), lastOutputAt: now.AddMinutes(-1));

        var reap = orch.FindReviewersToReap();

        await Assert.That(reap).Contains(("rev-old", "reviewer_ttl_expired"));
        await Assert.That(reap).Contains(("rev-idle", "reviewer_idle_expired"));
        await Assert.That(reap.Select(r => r.Id)).DoesNotContain("interactive");
        await Assert.That(reap.Select(r => r.Id)).DoesNotContain("rev-fresh");
    }

    [Test]
    public async Task FindReviewersToReap_disabled_when_bounds_are_zero() {
        var now = new DateTime(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc);
        await using var orch = BuildOrchestrator(
            new CaptureServerConnection(), new SpyPtyProcessFactory(), new Dictionary<string, IHostedAgentLauncher>(),
            configure: c => { c.ReviewerMaxLifetime = TimeSpan.Zero; c.ReviewerIdleTimeout = TimeSpan.Zero; });
        orch.ClockUtc = () => now;
        orch.SeedAgentForTest("rev", LaunchKind.ReviewFlow, status: "Running",
            createdAt: now.AddHours(-99), lastOutputAt: now.AddHours(-99));

        await Assert.That(orch.FindReviewersToReap()).IsEmpty();
    }
}
