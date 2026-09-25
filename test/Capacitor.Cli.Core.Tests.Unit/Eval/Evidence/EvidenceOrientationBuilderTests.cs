using System.Text;
using Capacitor.Cli.Core.Eval.Evidence;

namespace Capacitor.Cli.Core.Tests.Unit.Eval.Evidence;

/// <summary>The orientation names the scope, lists its completeness, seeds only whole pages under o-handles, stays within its
/// bytes, leaves a sentence where the summary is unavailable or unreadable, counts an unreadable outline as unfinished, and
/// reports a moved scope instead of building.</summary>
public class EvidenceOrientationBuilderTests : IDisposable {
    readonly EvidenceServerStub _stub = new();
    readonly HttpClient _http = new();

    public void Dispose() { _http.Dispose(); _stub.Dispose(); }

    static string Root => EvidenceServerStub.RootSource;

    EvidenceOrientationBuilder Builder() => new(new EvidenceReadClient(_http, _stub.Url, EvidenceServerStub.SessionId));

    static EvidenceScopeState Scope(bool complete = true, params string[] reasons) => new("v1", EvidenceServerStub.SessionId, complete, reasons,
        [new EvidenceSourceDto { SourceId = Root, Kind = "root", SessionId = EvidenceServerStub.SessionId, FirstRevision = 0, RevisionCutoff = 9, TurnCount = 2, Availability = "available" }],
        "tok", DateTimeOffset.UnixEpoch.AddHours(1), null);

    [Test]
    public async Task The_orientation_names_the_scope_and_seeds_whole_pages_within_its_bytes() {
        _stub.Route("GET", "evidence-turns", 200, EvidenceServerStub.TurnsPage(Root, [(0, 0, 4), (1, 5, 9)]));
        _stub.Route("GET", "evidence-calls/summary", 200, EvidenceServerStub.Summary());

        var o = await Builder().BuildAsync(Scope(), EvidenceBudgets.OrientationBytes, 65_536, CancellationToken.None);

        await Assert.That(o.FailedStatus).IsNull();
        await Assert.That(o.Text.StartsWith(EvidenceOrientationBuilder.ScopeSentence, StringComparison.Ordinal)).IsTrue();
        await Assert.That(o.Text.Contains("Scope is complete over these sources.")).IsTrue();
        await Assert.That(o.Pages.Select(p => p.Handle)).IsEquivalentTo(["o0", "o1", "o2"]);
        await Assert.That(o.Pages.Select(p => p.Tool)).IsEquivalentTo(["list_sources", "list_turns", "summarize_calls"]);
        await Assert.That(o.Pages.All(p => p.Seq == 0)).IsTrue();
        foreach (var page in o.Pages) await Assert.That(o.Text.Contains(page.Text)).IsTrue();
        await Assert.That(o.OutlinedTurns).IsEqualTo(2);
        await Assert.That(o.UnfinishedOutlines).IsEqualTo(0);
        await Assert.That(o.OutlineTurns).IsEquivalentTo([(Root, 0), (Root, 1)]);
        await Assert.That(Encoding.UTF8.GetByteCount(o.Text)).IsLessThanOrEqualTo(EvidenceBudgets.OrientationBytes);
        await Assert.That(_stub.Requests("evidence-turns").Single().RequestMessage.Query!["token"].Single()).IsEqualTo("tok");
    }

    [Test]
    public async Task An_incomplete_scope_names_every_reason() {
        _stub.Route("GET", "evidence-turns", 200, EvidenceServerStub.TurnsPage(Root, [(0, 0, 9)]));
        _stub.Route("GET", "evidence-calls/summary", 200, EvidenceServerStub.Summary());

        var o = await Builder().BuildAsync(Scope(false, "source_limit", "discovery_failed"), EvidenceBudgets.OrientationBytes, 65_536, CancellationToken.None);

        await Assert.That(o.Text.Contains("Scope is incomplete: source_limit, discovery_failed.")).IsTrue();
    }

    [Test]
    public async Task A_page_that_does_not_fit_is_left_out_whole_and_its_outline_counts_as_unfinished() {
        var turns = Enumerable.Range(0, 200).Select(i => (i, (long)i, (long)i));
        _stub.Route("GET", "evidence-turns", 200, EvidenceServerStub.TurnsPage(Root, turns));
        _stub.Route("GET", "evidence-calls/summary", 200, EvidenceServerStub.Summary());

        var o = await Builder().BuildAsync(Scope(), 2_000, 65_536, CancellationToken.None);

        await Assert.That(o.Pages.Any(p => p.Tool == "list_turns")).IsFalse();
        await Assert.That(o.Text.Contains($"{Root}#g1t0")).IsFalse();
        await Assert.That(o.UnfinishedOutlines).IsEqualTo(1);
        await Assert.That(Encoding.UTF8.GetByteCount(o.Text)).IsLessThanOrEqualTo(2_000);
        foreach (var page in o.Pages) await Assert.That(o.Text.Contains(page.Text)).IsTrue();
    }

    [Test]
    [Arguments(200, "unavailable", EvidenceOrientationBuilder.SummaryUnavailable)]
    [Arguments(503, "ready", EvidenceOrientationBuilder.SummaryBusy)]
    public async Task An_unavailable_or_busy_index_leaves_a_sentence_instead_of_the_summary(int status, string indexState, string sentence) {
        _stub.Route("GET", "evidence-turns", 200, EvidenceServerStub.TurnsPage(Root, [(0, 0, 9)]));
        _stub.Route("GET", "evidence-calls/summary", status, status == 200 ? EvidenceServerStub.Summary(indexState) : """{"code":"index_busy","detail":"the evidence index is busy; retry"}""");

        var o = await Builder().BuildAsync(Scope(), EvidenceBudgets.OrientationBytes, 65_536, CancellationToken.None);

        await Assert.That(o.Text.Contains(sentence)).IsTrue();
        await Assert.That(o.Pages.Any(p => p.Tool == "summarize_calls")).IsFalse();
    }

    [Test]
    public async Task A_moved_scope_is_reported_instead_of_building() {
        _stub.Route("GET", "evidence-turns", 409, """{"code":"scope_moved","current_version":"v2"}""");

        var o = await Builder().BuildAsync(Scope(), EvidenceBudgets.OrientationBytes, 65_536, CancellationToken.None);

        await Assert.That(o.FailedStatus).IsEqualTo(409);
        await Assert.That(o.Pages).IsEmpty();
    }

    [Test]
    public async Task An_unreadable_outline_or_summary_is_left_out_instead_of_failing_the_build() {
        _stub.Route("GET", "evidence-turns", 200, "<html>not json</html>");
        _stub.Route("GET", "evidence-calls/summary", 200, "[1,2]");

        var o = await Builder().BuildAsync(Scope(), EvidenceBudgets.OrientationBytes, 65_536, CancellationToken.None);

        await Assert.That(o.FailedStatus).IsNull();
        await Assert.That(o.Pages.Select(p => p.Tool)).IsEquivalentTo(["list_sources"]);
        await Assert.That(o.UnfinishedOutlines).IsEqualTo(1);
        await Assert.That(o.Text.Contains(EvidenceOrientationBuilder.SummaryUnavailable)).IsTrue();
    }
}
