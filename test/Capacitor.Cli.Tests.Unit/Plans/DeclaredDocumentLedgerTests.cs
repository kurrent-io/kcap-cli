using Capacitor.Cli.Plans;

namespace Capacitor.Cli.Tests.Unit.Plans;

public class DeclaredDocumentLedgerTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Server = "https://kcap.example";

    static readonly DeclaredDocument Spec = new("/repo/docs/spec.md", "spec", "/repo/docs/design.md", "wi-1", "/repo", "/repo");

    DeclaredDocumentLedger Ledger() => new(Config.Root, TimeProvider.System);

    [Test]
    public async Task Finds_a_declared_document_per_session_and_path() {
        var ledger = Ledger();

        ledger.Record("s1", Spec, Server);

        await Assert.That(ledger.Find("s1", "/repo/docs/spec.md", Server)).IsEqualTo(Spec);
        await Assert.That(ledger.Find("s1", "/repo/docs/other.md", Server)).IsNull();
        await Assert.That(ledger.Find("s2", "/repo/docs/spec.md", Server)).IsNull();
    }

    [Test]
    public async Task Never_hands_a_declaration_to_a_server_that_did_not_accept_it() {
        var ledger = Ledger();

        ledger.Record("s1", Spec, Server);

        await Assert.That(ledger.Find("s1", Spec.Path, "https://other.example")).IsNull();
    }

    [Test]
    public async Task A_later_declaration_of_the_same_path_replaces_the_earlier_one() {
        var ledger = Ledger();
        var replanned = Spec with { Kind = "plan", ArguesFrom = null, WorkItemId = null };

        ledger.Record("s1", Spec, Server);
        ledger.Record("s1", replanned, Server);

        await Assert.That(ledger.Find("s1", Spec.Path, Server)).IsEqualTo(replanned);
    }

    [Test]
    public async Task Finds_a_path_cased_differently_where_the_filesystem_ignores_case() {
        var ledger = Ledger();

        ledger.Record("s1", Spec, Server);

        var found = ledger.Find("s1", "/repo/docs/SPEC.md", Server);
        if (OperatingSystem.IsLinux()) await Assert.That(found).IsNull();
        else await Assert.That(found).IsEqualTo(Spec);
    }

    [Test]
    public async Task Recording_reaps_sessions_idle_longer_than_the_retention() {
        var ledger = Ledger();
        ledger.Record("old", Spec, Server);
        Age("old");

        ledger.Record("new", Spec, Server);

        await Assert.That(ledger.Find("old", Spec.Path, Server)).IsNull();
        await Assert.That(ledger.Find("new", Spec.Path, Server)).IsEqualTo(Spec);
    }

    [Test]
    public async Task An_edit_keeps_a_long_session_from_being_reaped() {
        var ledger = Ledger();
        ledger.Record("long", Spec, Server);
        Age("long");

        await Assert.That(ledger.Find("long", Spec.Path, Server)).IsEqualTo(Spec);
        ledger.Record("other", Spec, Server);

        await Assert.That(ledger.Find("long", Spec.Path, Server)).IsEqualTo(Spec);
    }

    [Test]
    public async Task A_document_is_held_by_one_refresh_at_a_time() {
        var ledger = Ledger();

        using (var held = await ledger.LockAsync("s1", Spec.Path, TimeSpan.Zero)) {
            await Assert.That(held).IsNotNull();
            await Assert.That(await ledger.LockAsync("s1", Spec.Path, TimeSpan.FromMilliseconds(100))).IsNull();
            using var other = await ledger.LockAsync("s1", "/repo/docs/other.md", TimeSpan.Zero);
            await Assert.That(other).IsNotNull();
        }

        using var again = await ledger.LockAsync("s1", Spec.Path, TimeSpan.Zero);
        await Assert.That(again).IsNotNull();
    }

    void Age(string sessionId) =>
        Directory.SetLastWriteTimeUtc(Config.Root.Path("declared-documents", sessionId), DateTime.UtcNow - DeclaredDocumentLedger.Retention - TimeSpan.FromDays(1));
}
