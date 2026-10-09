using Capacitor.Cli.Harness.GrokBot;

namespace Capacitor.Cli.Tests.Unit.Harness.GrokBot;

/// <summary>Pins the watcher's spacing after failures and that an unchanged failure is reported once.</summary>
public class GrokBotBackoffTests {
    static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);
    static readonly TimeSpan Ceiling  = TimeSpan.FromMinutes(5);

    [Test]
    public async Task Healthy_cycles_wait_the_polling_interval() {
        var backoff = new GrokBotBackoff(Interval, Ceiling);

        await Assert.That(backoff.NextDelay).IsEqualTo(Interval);
        await Assert.That(backoff.Succeeded()).IsEqualTo(0);
        await Assert.That(backoff.NextDelay).IsEqualTo(Interval);
    }

    [Test]
    public async Task Each_failure_doubles_the_wait_up_to_the_ceiling() {
        var backoff = new GrokBotBackoff(Interval, Ceiling);
        var delays  = new List<double>();

        for (var i = 0; i < 8; i++) {
            backoff.Failed("down");
            delays.Add(backoff.NextDelay.TotalSeconds);
        }

        await Assert.That(delays).IsEquivalentTo([10.0, 20, 40, 80, 160, 300, 300, 300]);
    }

    [Test]
    public async Task A_repeated_reason_is_reported_once_and_a_new_one_again() {
        var backoff = new GrokBotBackoff(Interval, Ceiling);

        await Assert.That(backoff.Failed("401")).IsEqualTo("401");
        await Assert.That(backoff.Failed("401")).IsNull();
        await Assert.That(backoff.Failed("503")).IsEqualTo("503");
        await Assert.That(backoff.Failed("503")).IsNull();
    }

    [Test]
    public async Task Success_reports_the_failures_it_ended_and_resets() {
        var backoff = new GrokBotBackoff(Interval, Ceiling);
        backoff.Failed("401");
        backoff.Failed("401");
        backoff.Failed("401");

        await Assert.That(backoff.Succeeded()).IsEqualTo(3);
        await Assert.That(backoff.NextDelay).IsEqualTo(Interval);
        await Assert.That(backoff.Failed("401")).IsEqualTo("401");
    }
}
