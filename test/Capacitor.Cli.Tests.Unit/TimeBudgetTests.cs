using System.Diagnostics;
using Capacitor.Cli;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>
/// <see cref="TimeBudget.RunCappedAsync"/> bounds best-effort hook pre-work (watcher
/// kill + inline transcript drain) so a slow/retrying remote call can't consume the
/// whole SessionEnd hook timeout and starve the critical session-end POST that follows
/// — the failure that left sessions stuck "Active" after a clean exit.
/// </summary>
public class TimeBudgetTests {
    [Test]
    public async Task RunCappedAsync_ReturnsAtCap_WhenWorkExceedsIt() {
        var sw = Stopwatch.StartNew();

        await TimeBudget.RunCappedAsync(() => Task.Delay(TimeSpan.FromSeconds(5)), TimeSpan.FromMilliseconds(200));

        sw.Stop();

        // Returns at ~the cap, NOT after the 5s work — proves a slow drain is abandoned
        // so the session-end POST still gets to run inside the hook budget.
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(2));
    }

    [Test]
    public async Task RunCappedAsync_ReturnsWhenWorkCompletes_WithoutWaitingForCap() {
        var sw = Stopwatch.StartNew();

        await TimeBudget.RunCappedAsync(() => Task.CompletedTask, TimeSpan.FromSeconds(30));

        sw.Stop();

        // Fast work returns immediately — the cap is a ceiling, not a fixed delay.
        await Assert.That(sw.Elapsed).IsLessThan(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task RunCappedAsync_PropagatesException_WhenWorkFailsWithinCap() {
        await Assert.That(async () =>
                await TimeBudget.RunCappedAsync(
                    () => Task.FromException(new InvalidOperationException("drain blew up")),
                    TimeSpan.FromSeconds(30)
                )
            )
            .Throws<InvalidOperationException>();
    }
}
