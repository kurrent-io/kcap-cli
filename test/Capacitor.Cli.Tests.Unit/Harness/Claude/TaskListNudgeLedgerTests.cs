using Capacitor.Cli.Harness.Claude;

namespace Capacitor.Cli.Tests.Unit.Harness.Claude;

public class TaskListNudgeLedgerTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task Remembers_per_session_until_the_session_is_evicted() {
        var ledger = new TaskListNudgeLedger(Config.Root);

        ledger.RecordPlan("s1");
        ledger.RecordNudge("s1", ClaudeTaskListEvent.Moment.Created);

        await Assert.That(ledger.HasPlan("s1")).IsTrue();
        await Assert.That(ledger.WasNudged("s1", ClaudeTaskListEvent.Moment.Created)).IsTrue();
        await Assert.That(ledger.WasNudged("s1", ClaudeTaskListEvent.Moment.Completed)).IsFalse();
        await Assert.That(ledger.HasPlan("s2")).IsFalse();

        ledger.Evict("s1");

        await Assert.That(ledger.HasPlan("s1")).IsFalse();
        await Assert.That(ledger.WasNudged("s1", ClaudeTaskListEvent.Moment.Created)).IsFalse();
    }
}
