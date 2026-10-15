using Capacitor.App.Services;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

public class ActiveDisplayGateTests {
    const int NoActiveDisplay = -6661;

    [Test]
    public async Task A_display_link_that_can_be_created_starts_the_app_at_once() {
        var time    = new FakeTimeProvider();
        var waiting = 0;

        var wait = ActiveDisplayGate.WaitAsync(() => 0, time, onWaiting: () => waiting++);

        await Assert.That(wait.IsCompletedSuccessfully).IsTrue();
        await Assert.That(waiting).IsEqualTo(0);
    }

    [Test]
    public async Task A_refused_display_link_waits_until_one_can_be_created_and_reports_the_wait_once() {
        var time    = new FakeTimeProvider();
        var codes   = new Queue<int?>([NoActiveDisplay, NoActiveDisplay, 0]);
        var waiting = 0;

        var wait = ActiveDisplayGate.WaitAsync(codes.Dequeue, time, onWaiting: () => waiting++);

        await Assert.That(wait.IsCompleted).IsFalse();

        time.Advance(ActiveDisplayGate.PollInterval);
        await Task.Delay(50);
        await Assert.That(wait.IsCompleted).IsFalse();

        time.Advance(ActiveDisplayGate.PollInterval);
        await wait.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(codes.Count).IsEqualTo(0);
        await Assert.That(waiting).IsEqualTo(1);
    }

    [Test]
    public async Task A_probe_that_cannot_run_does_not_hold_the_app() {
        var time = new FakeTimeProvider();

        var wait = ActiveDisplayGate.WaitAsync(() => null, time, onWaiting: () => { });

        await Assert.That(wait.IsCompletedSuccessfully).IsTrue();
    }
}
