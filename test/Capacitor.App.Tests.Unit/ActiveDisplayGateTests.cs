using Capacitor.App.Services;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

public class ActiveDisplayGateTests {
    [Test]
    public async Task A_lit_display_starts_the_app_at_once() {
        var time    = new FakeTimeProvider();
        var waiting = 0;

        var wait = ActiveDisplayGate.WaitAsync(() => 2, time, onWaiting: () => waiting++);

        await Assert.That(wait.IsCompletedSuccessfully).IsTrue();
        await Assert.That(waiting).IsEqualTo(0);
    }

    [Test]
    public async Task A_dark_screen_waits_until_a_display_wakes_and_reports_the_wait_once() {
        var time    = new FakeTimeProvider();
        var counts  = new Queue<int>([0, 0, 2]);
        var waiting = 0;

        var wait = ActiveDisplayGate.WaitAsync(counts.Dequeue, time, onWaiting: () => waiting++);

        await Assert.That(wait.IsCompleted).IsFalse();

        time.Advance(ActiveDisplayGate.PollInterval);
        await Task.Delay(50);
        await Assert.That(wait.IsCompleted).IsFalse();

        time.Advance(ActiveDisplayGate.PollInterval);
        await wait.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.That(counts.Count).IsEqualTo(0);
        await Assert.That(waiting).IsEqualTo(1);
    }

    [Test]
    public async Task A_failed_display_query_does_not_hold_the_app() {
        var time = new FakeTimeProvider();

        var wait = ActiveDisplayGate.WaitAsync(() => -1, time, onWaiting: () => { });

        await Assert.That(wait.IsCompletedSuccessfully).IsTrue();
    }
}
