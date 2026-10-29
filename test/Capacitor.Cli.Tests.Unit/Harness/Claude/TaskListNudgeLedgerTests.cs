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

    [Test]
    public async Task An_unwritable_marker_is_no_claim() {
        var ledger = new TaskListNudgeLedger(Config.Root);
        Directory.CreateDirectory(Config.Root.Path("task-list"));
        File.WriteAllText(Config.Root.Path("task-list", "s1"), "");

        await Assert.That(ledger.TryClaimNudge("s1", ClaudeTaskListEvent.Moment.Created)).IsFalse();
    }

    [Test]
    public async Task A_whole_list_replaces_the_tasks_and_an_update_merges_into_them() {
        var ledger = new TaskListNudgeLedger(Config.Root);

        ledger.SaveTasks("s1", [new("a1", 1, "Task 1", "pending"), new("b2", 2, "Task 2", "pending")], whole: true);
        ledger.SaveTasks("s1", [new("b2", 2, "Task 2", "in_progress")], whole: false);
        var merged = ledger.LoadTasks("s1");
        ledger.SaveTasks("s1", [new("c3", 1, "Task 1", "pending")], whole: true);

        await Assert.That(merged.OrderBy(t => t.Ordinal).Select(t => t.Status)).IsEquivalentTo(new[] { "pending", "in_progress" });
        await Assert.That(ledger.LoadTasks("s1").Select(t => t.TaskId)).IsEquivalentTo(new[] { "c3" });
    }
}
