using Capacitor.Cli.Harness.Claude;

namespace Capacitor.Cli.Tests.Unit.Harness.Claude;

public class PlanReadNudgeLedgerTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [Test]
    public async Task Remembers_a_document_per_session_until_the_session_is_evicted() {
        var ledger = new PlanReadNudgeLedger(Config.Root);

        ledger.Record("s1", "/repo/docs/plans/a.md");

        await Assert.That(ledger.WasNudged("s1", "/repo/docs/plans/a.md")).IsTrue();
        await Assert.That(ledger.WasNudged("s1", "/repo/docs/plans/b.md")).IsFalse();
        await Assert.That(ledger.WasNudged("s2", "/repo/docs/plans/a.md")).IsFalse();

        ledger.Evict("s1");

        await Assert.That(ledger.WasNudged("s1", "/repo/docs/plans/a.md")).IsFalse();
    }
}
