using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class RestartDecisionTests {
    static readonly BinaryStat A = new(100, 1000);

    [Test]
    public async Task Fires_when_pending_and_idle() =>
        await Assert.That(RestartDecision.ShouldFire(pending: true, busy: false, force: false, selfDetected: false, atTurnBoundary: true, waitExpired: false)).IsTrue();

    [Test]
    public async Task Does_not_fire_when_busy() =>
        await Assert.That(RestartDecision.ShouldFire(pending: true, busy: true, force: false, selfDetected: false, atTurnBoundary: true, waitExpired: false)).IsFalse();

    [Test]
    public async Task Force_fires_without_a_boundary_when_not_busy() =>
        await Assert.That(RestartDecision.ShouldFire(pending: true, busy: false, force: true, selfDetected: true, atTurnBoundary: false, waitExpired: false)).IsTrue();

    [Test]
    public async Task Does_not_fire_when_not_pending() =>
        await Assert.That(RestartDecision.ShouldFire(pending: false, busy: false, force: true, selfDetected: false, atTurnBoundary: true, waitExpired: false)).IsFalse();

    [Test]
    public async Task Self_detected_waits_for_a_turn_boundary() =>
        await Assert.That(RestartDecision.ShouldFire(true, busy: false, force: false, selfDetected: true, atTurnBoundary: false, waitExpired: false)).IsFalse();

    [Test]
    public async Task Self_detected_fires_when_the_wait_expires() =>
        await Assert.That(RestartDecision.ShouldFire(true, busy: false, force: false, selfDetected: true, atTurnBoundary: false, waitExpired: true)).IsTrue();

    [Test]
    public async Task Explicit_request_fires_without_a_boundary() =>
        await Assert.That(RestartDecision.ShouldFire(true, busy: false, force: false, selfDetected: false, atTurnBoundary: false, waitExpired: false)).IsTrue();

    [Test]
    public async Task Busy_blocks_even_a_boundary_or_expired_wait() =>
        await Assert.That(RestartDecision.ShouldFire(true, busy: true, force: false, selfDetected: true, atTurnBoundary: true, waitExpired: true)).IsFalse();

    [Test]
    public async Task Size_change_is_a_change() =>
        await Assert.That(RestartDecision.BinaryChanged(A, A with { Size = 200 })).IsTrue();

    [Test]
    public async Task Mtime_change_is_a_change() =>
        await Assert.That(RestartDecision.BinaryChanged(A, A with { MtimeTicks = 2000 })).IsTrue();

    [Test]
    public async Task A_new_current_target_is_a_change_with_size_and_mtime_equal() =>
        await Assert.That(RestartDecision.BinaryChanged(A with { Target = "versions/1.0.0" }, A with { Target = "versions/1.1.0" })).IsTrue();

    [Test]
    public async Task Identical_is_not_a_change() =>
        await Assert.That(RestartDecision.BinaryChanged(A, A)).IsFalse();

    [Test]
    public async Task Null_current_is_transient_not_a_change() =>
        await Assert.That(RestartDecision.BinaryChanged(A, null)).IsFalse();

    [Test]
    public async Task Null_baseline_is_not_a_change() =>
        await Assert.That(RestartDecision.BinaryChanged(null, A)).IsFalse();
}
