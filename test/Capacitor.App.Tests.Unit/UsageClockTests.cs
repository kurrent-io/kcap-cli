using Capacitor.App.Services;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

public class UsageClockTests {
    [Test]
    public async Task Emits_now_on_subscribe_and_every_30_seconds() {
        var time = new FakeTimeProvider();
        var start = time.GetUtcNow();
        var seen = new List<DateTimeOffset>();
        using var sub = UsageClock.Create(time).Subscribe(t => { lock (seen) seen.Add(t); });
        DateTimeOffset[] Seen() { lock (seen) return [.. seen]; }

        await Assert.That(Seen()).IsEquivalentTo([start], CollectionOrdering.Matching);
        time.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1));
        await Assert.That(Seen().Length).IsEqualTo(1);
        time.Advance(TimeSpan.FromTicks(1));
        time.Advance(TimeSpan.FromSeconds(30));

        await Assert.That(Seen()).IsEquivalentTo(
            [start, start.AddSeconds(30), start.AddSeconds(60)], CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_late_subscriber_gets_now_at_once_and_shares_the_ticks() {
        var time = new FakeTimeProvider();
        var start = time.GetUtcNow();
        var clock = UsageClock.Create(time);
        using var first = clock.Subscribe(_ => { });
        time.Advance(TimeSpan.FromSeconds(10));
        var seen = new List<DateTimeOffset>();
        using var late = clock.Subscribe(t => { lock (seen) seen.Add(t); });

        time.Advance(TimeSpan.FromSeconds(20));

        DateTimeOffset[] snapshot;
        lock (seen) snapshot = [.. seen];
        await Assert.That(snapshot).IsEquivalentTo([start.AddSeconds(10), start.AddSeconds(30)], CollectionOrdering.Matching);
    }
}
