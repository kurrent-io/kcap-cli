using Capacitor.Cli.Harness.Claude;

namespace Capacitor.Cli.Tests.Unit.Harness.Claude;

public class TaskListNudgeLedgerTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task Remembers_per_session_until_the_session_is_evicted() {
        var ledger = new TaskListNudgeLedger(Config.Root);

        ledger.RecordPlan("s1");

        await Assert.That(ledger.HasPlan("s1")).IsTrue();
        await Assert.That(ledger.HasPlan("s2")).IsFalse();
        await Assert.That(ledger.TryClaimNudge("s1", ClaudeTaskListEvent.Moment.Created)).IsTrue();

        ledger.Evict("s1");

        await Assert.That(ledger.HasPlan("s1")).IsFalse();
        await Assert.That(ledger.TryClaimNudge("s1", ClaudeTaskListEvent.Moment.Created)).IsTrue();
    }

    [Test]
    public async Task A_reminder_is_claimed_once_until_released() {
        var ledger = new TaskListNudgeLedger(Config.Root);

        var claims = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => ledger.TryClaimNudge("s1", ClaudeTaskListEvent.Moment.Created))));

        await Assert.That(claims.Count(c => c)).IsEqualTo(1);
        await Assert.That(ledger.TryClaimNudge("s1", ClaudeTaskListEvent.Moment.Completed)).IsTrue();

        ledger.ReleaseNudge("s1", ClaudeTaskListEvent.Moment.Created);

        await Assert.That(ledger.TryClaimNudge("s1", ClaudeTaskListEvent.Moment.Created)).IsTrue();
    }
}
