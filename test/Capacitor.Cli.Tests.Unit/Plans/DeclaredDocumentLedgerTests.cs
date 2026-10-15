using Capacitor.Cli.Plans;

namespace Capacitor.Cli.Tests.Unit.Plans;

public class DeclaredDocumentLedgerTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static readonly DeclaredDocument Spec = new("/repo/docs/spec.md", "spec", "/repo/docs/design.md", "wi-1", "/repo", "/repo");

    [Test]
    public async Task Finds_a_declared_document_per_session_until_the_session_is_evicted() {
        var ledger = new DeclaredDocumentLedger(Config.Root, TimeProvider.System);

        ledger.Record("s1", Spec);

        await Assert.That(ledger.Find("s1", "/repo/docs/spec.md")).IsEqualTo(Spec);
        await Assert.That(ledger.Find("s1", "/repo/docs/other.md")).IsNull();
        await Assert.That(ledger.Find("s2", "/repo/docs/spec.md")).IsNull();

        ledger.Evict("s1");

        await Assert.That(ledger.Find("s1", "/repo/docs/spec.md")).IsNull();
    }

    [Test]
    public async Task A_later_declaration_of_the_same_path_replaces_the_earlier_one() {
        var ledger = new DeclaredDocumentLedger(Config.Root, TimeProvider.System);

        ledger.Record("s1", Spec);
        ledger.Record("s1", Spec with { Kind = "plan", ArguesFrom = null, WorkItemId = null });

        await Assert.That(ledger.Find("s1", "/repo/docs/spec.md")).IsEqualTo(Spec with { Kind = "plan", ArguesFrom = null, WorkItemId = null });
    }

    [Test]
    public async Task Recording_reaps_sessions_older_than_the_retention() {
        var ledger = new DeclaredDocumentLedger(Config.Root, TimeProvider.System);
        ledger.Record("old", Spec);
        Directory.SetLastWriteTimeUtc(Config.Root.Path("declared-documents", "old"), DateTime.UtcNow - DeclaredDocumentLedger.Retention - TimeSpan.FromDays(1));

        ledger.Record("new", Spec);

        await Assert.That(ledger.Find("old", Spec.Path)).IsNull();
        await Assert.That(ledger.Find("new", Spec.Path)).IsEqualTo(Spec);
    }
}
