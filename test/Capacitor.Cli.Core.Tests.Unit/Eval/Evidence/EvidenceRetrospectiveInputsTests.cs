using Capacitor.Cli.Core.Eval.Contracts;
using Capacitor.Cli.Core.Eval.Evidence;

namespace Capacitor.Cli.Core.Tests.Unit.Eval.Evidence;

public class EvidenceRetrospectiveInputsTests : IDisposable {
    readonly EvidenceServerStub _stub = new();
    readonly HttpClient _http = new();

    public void Dispose() { _http.Dispose(); _stub.Dispose(); }

    static string Root => EvidenceServerStub.RootSource;

    EvidenceRetrospectiveInputs Inputs() => new(new EvidenceReadClient(_http, _stub.Url, EvidenceServerStub.SessionId));

    static EvidenceScopeState Scope() => new("v1", EvidenceServerStub.SessionId, true, [],
        [new EvidenceSourceDto { SourceId = Root, Kind = "root", SessionId = EvidenceServerStub.SessionId, FirstRevision = 0, RevisionCutoff = 9, TurnCount = 2, Availability = "available" }],
        "tok", DateTimeOffset.UnixEpoch.AddHours(1), null);

    static EvalQuestionAssessment Assessment(string id, string? outcome, int? score, params string[] refs) => new() {
        Category = "quality", QuestionId = id, Outcome = outcome, Score = score, Verdict = score is { } s ? Capacitor.Cli.Core.Eval.EvalService.VerdictForScore(s) : null, Finding = "f",
        EvidenceCoverage = new EvalEvidenceCoverage { PolicyVersion = "coverage-v2", Citations = [.. refs.Select(r => new EvalEvidenceCitation { Ref = r, Digest = new string('a', 64) })] }
    };

    void Events(long revision, string text) =>
        _stub.Route("GET", "evidence-events", 200, EvidenceServerStub.EventsPage(Root, [EvidenceServerStub.EventEntry(Root, revision, text)]), new Dictionary<string, string> { ["ref"] = $"{Root}@{revision}" });

    [Test]
    public async Task Cited_evidence_is_re_read_ranked_and_introduced_by_the_scope_summary() {
        Events(1, "passing evidence");
        Events(2, "failing evidence");
        Events(3, "unassessed evidence");
        var assessments = new[] {
            Assessment("a-pass", "assessed", 5, $"{Root}@1"),
            Assessment("b-fail", "assessed", 1, $"{Root}@2"),
            Assessment("c-insufficient", "insufficient_evidence", null, $"{Root}@3")
        };

        var (trace, failed) = await Inputs().BuildTraceAsync(Scope(), assessments, 200_000, null, CancellationToken.None);

        await Assert.That(failed).IsNull();
        await Assert.That(trace.StartsWith("Scope v1: 1 source(s), complete over the root and its subagent lanes. No budget stops.", StringComparison.Ordinal)).IsTrue();
        await Assert.That(trace.Contains("Cited evidence (3):")).IsTrue();
        var fail = trace.IndexOf("failing evidence", StringComparison.Ordinal);
        var unassessed = trace.IndexOf("unassessed evidence", StringComparison.Ordinal);
        var pass = trace.IndexOf("passing evidence", StringComparison.Ordinal);
        await Assert.That(fail).IsLessThan(unassessed);
        await Assert.That(unassessed).IsLessThan(pass);
    }

    [Test]
    public async Task An_unreadable_ref_leaves_a_fixed_line() {
        _stub.Route("GET", "evidence-events", 404, "");

        var (trace, failed) = await Inputs().BuildTraceAsync(Scope(), [Assessment("q", "assessed", 2, $"{Root}@7")], 200_000, null, CancellationToken.None);

        await Assert.That(failed).IsNull();
        await Assert.That(trace.Contains($"{Root}@7: {EvidenceRetrospectiveInputs.NoLongerReadable}")).IsTrue();
    }

    [Test]
    public async Task The_excerpt_budget_omits_later_refs_and_says_so() {
        Events(1, new string('x', 1_000));
        Events(2, new string('y', 1_000));
        Events(3, new string('z', 1_000));

        var (trace, _) = await Inputs().BuildTraceAsync(Scope(), [Assessment("q", "assessed", 2, $"{Root}@1", $"{Root}@2", $"{Root}@3")], 1_200, null, CancellationToken.None);

        await Assert.That(trace.Contains("Cited evidence (1 of 3 shown; 2 omitted for space):")).IsTrue();
        await Assert.That(trace.Contains("yyyy")).IsFalse();
    }

    [Test]
    public async Task An_excerpt_is_cut_to_its_character_limit() {
        Events(1, new string('x', 5_000));

        var (trace, _) = await Inputs().BuildTraceAsync(Scope(), [Assessment("q", "assessed", 2, $"{Root}@1")], 200_000, null, CancellationToken.None);

        await Assert.That(trace.Contains(new string('x', EvidenceRetrospectiveInputs.ExcerptChars))).IsTrue();
        await Assert.That(trace.Contains(new string('x', EvidenceRetrospectiveInputs.ExcerptChars + 1))).IsFalse();
    }

    [Test]
    public async Task A_turn_citation_is_read_through_its_event_window() {
        _stub.Route("GET", "evidence-turns", 200, EvidenceServerStub.TurnsPage(Root, [(1, 5, 9)]));
        _stub.Route("GET", "evidence-events", 200, EvidenceServerStub.EventsPage(Root, [EvidenceServerStub.EventEntry(Root, 5, "turn window text")]), new Dictionary<string, string> { ["ref"] = $"{Root}@5-9" });

        var (trace, _) = await Inputs().BuildTraceAsync(Scope(), [Assessment("q", "assessed", 2, $"{Root}#g1t1")], 200_000, null, CancellationToken.None);

        await Assert.That(trace.Contains("turn window text")).IsTrue();
    }

    [Test]
    public async Task A_moved_scope_during_the_re_read_is_run_fatal() {
        _stub.Route("GET", "evidence-events", 409, """{"code":"scope_moved","current_version":"v2"}""");

        var (_, failed) = await Inputs().BuildTraceAsync(Scope(), [Assessment("q", "assessed", 2, $"{Root}@1")], 200_000, null, CancellationToken.None);

        await Assert.That(failed).IsEqualTo(409);
    }

    [Test]
    [Arguments("event")]
    [Arguments("turn")]
    public async Task An_answer_that_does_not_parse_leaves_the_fixed_line(string form) {
        _stub.Route("GET", "evidence-events", 200, "<html>not json</html>");
        _stub.Route("GET", "evidence-turns", 200, "<html>not json</html>");
        _stub.Route("GET", "evidence-body", 200, "<html>not json</html>");
        var reference = form == "event" ? $"{Root}@1" : $"{Root}#g1t1";

        var (trace, failed) = await Inputs().BuildTraceAsync(Scope(), [Assessment("q", "assessed", 2, reference)], 200_000, null, CancellationToken.None);

        await Assert.That(failed).IsNull();
        await Assert.That(trace.Contains($"{reference}: {EvidenceRetrospectiveInputs.NoLongerReadable}")).IsTrue();
    }
}
