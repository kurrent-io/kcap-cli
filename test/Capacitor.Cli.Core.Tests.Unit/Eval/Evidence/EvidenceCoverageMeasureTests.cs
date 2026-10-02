using Capacitor.Cli.Core.Eval;
using Capacitor.Cli.Core.Eval.Contracts;
using Capacitor.Cli.Core.Eval.Evidence;

namespace Capacitor.Cli.Core.Tests.Unit.Eval.Evidence;

/// <summary>Coverage comes from the ledger and manifest alone: each omission kind appears exactly when its evidence was left
/// undelivered and is counted once however often it was re-read; exempt bodies never count; the record is empty only when
/// everything was delivered; a footer budget stop outranks judge_stopped; and the iteration-cap detail validates.</summary>
public class EvidenceCoverageMeasureTests {
    const string Root  = "AgentSession-r";
    const string Lane  = "AgentSubsession-r-a1";
    const string Lane2 = "AgentSubsession-r-a2";

    static EvidenceSourceDto Src(string id, long first, long cutoff, int? turns, bool available = true) => new() {
        SourceId = id, Kind = id == Root ? "root" : "subagent", SessionId = "r", FirstRevision = first, RevisionCutoff = cutoff, TurnCount = turns,
        Availability = available ? "available" : "unavailable"
    };

    static EvidenceScopeState Scope(params EvidenceSourceDto[] sources) => Scope(true, [], sources);

    static EvidenceScopeState Scope(bool complete, string[] reasons, params EvidenceSourceDto[] sources) =>
        new("v1", "r", complete, reasons, sources, "tok", DateTimeOffset.UnixEpoch.AddHours(1), null);

    static readonly JudgeLedgerHeader Header = new("run", "safety/q1", "v1", new EvidenceRunBudgets(48, 600_000, 65_536), null, DateTimeOffset.UnixEpoch);

    static JudgeLedger Ledger(IEnumerable<JudgeLedgerPage> pages, JudgeLedgerFooter? footer = null) => new(Header, [.. pages], [], footer);

    static JudgeLedgerFooter Footer(string? stop, long delivered = 1_000, int calls = 3, params string[] refused) => new(calls, delivered, stop, refused, DateTimeOffset.UnixEpoch);

    static int _seq;

    static JudgeLedgerPage Outline(string handle, string source, bool hasNext, params (int Index, long Start, long End, string State)[] turns) {
        var rows = string.Join(",", turns.Select((t, i) =>
            $$"""{"cite":"{{handle}}.{{i + 1}}","turn_ref":"{{source}}#g1t{{t.Index}}","range_state":"{{t.State}}","index":{{t.Index}},"start_revision":{{t.Start}},"end_revision":{{t.End}} }"""));
        var text = $$"""{"page":"{{handle}}","has_next":{{(hasNext ? "true" : "false")}},"scope_version":"v1","source_id":"{{source}}","turns":[{{rows}}] }""";
        return new(++_seq, handle, "list_turns", $$"""{"source":"{{source}}"}""", source, text, [], [.. turns.Select(t => (source, t.Index))], [], [],
            turns.Select((t, i) => ($"{handle}.{i + 1}", $"{source}#g1t{t.Index}")).ToDictionary(), hasNext, hasNext ? "cursor-1" : null);
    }

    static JudgeLedgerPage Events(string handle, string source, long from, long to, bool hasNext = false, IReadOnlyList<(string Ref, string Field, int? Ordinal)>? bodies = null) =>
        new(++_seq, handle, "read_events", $$"""{"ref":"{{source}}@{{from}}-{{to}}"}""", source, $$"""{"page":"{{handle}}","has_next":false}""",
            [(source, from, to)], [], bodies ?? [], [], new Dictionary<string, string>(), hasNext, hasNext ? "cursor-2" : null);

    static JudgeLedgerPage Body(string handle, string reference, string field, int? ordinal) =>
        new(++_seq, handle, "read_body", "{}", null, "{}", [], [], [(reference, field, ordinal)], [], new Dictionary<string, string>(), false, null);

    static JudgeLedgerPage Continuation(string handle, string continued, string tool, string? source) =>
        new(++_seq, handle, tool, $$"""{"page":"{{continued}}","next":true}""", source, "{}", [], [], [], [], new Dictionary<string, string>(), false, null);

    static int? Count(EvalEvidenceCoverage c, string kind) => c.Omissions.Where(o => o.Kind == kind).Sum(o => o.Count) is var n and > 0 ? n : null;

    [Test]
    public async Task Each_undelivered_kind_is_reported_once_under_re_fetch() {
        var scope  = Scope(Src(Root, 0, 9, 2), Src(Lane, 0, 4, 1));
        var output = ($"{Root}@3", "output", (int?)null);
        var ledger = Ledger([
            Outline("o1", Root, hasNext: true, (0, 0, 4, "ok"), (1, 5, 9, "ok")),
            Events("p1", Root, 0, 4, bodies: [output]),
            Events("p2", Root, 0, 4, bodies: [output])
        ]);

        var c = EvidenceCoverageMeasure.ForRetrieval(scope, ledger, []);

        await Assert.That(Count(c, EvalOmissionKinds.TurnsNotFetched)).IsEqualTo(1);
        await Assert.That(Count(c, EvalOmissionKinds.PagesNotFetched)).IsEqualTo(1);
        await Assert.That(Count(c, EvalOmissionKinds.BodiesNotFetched)).IsEqualTo(1);
        await Assert.That(Count(c, EvalOmissionKinds.SourcesNotConsulted)).IsEqualTo(1);
        await Assert.That(c.SourcesConsulted).IsEquivalentTo([Root]);
        await Assert.That(c.StopReason).IsEqualTo(EvalStopReasons.JudgeStopped);
        await Assert.That(c.PolicyVersion).IsEqualTo("coverage-v2");
        await Assert.That(c.ScopeVersion).IsEqualTo("v1");
        await Assert.That(c.Validate()).IsNull();
    }

    [Test]
    public async Task A_followed_page_an_opened_body_and_a_shared_call_key_clear_their_omissions() {
        var scope  = Scope(Src(Root, 0, 4, 1));
        var ledger = Ledger([
            Outline("o1", Root, hasNext: true, (0, 0, 4, "ok")),
            Continuation("p1", "o1", "list_turns", Root),
            Events("p2", Root, 0, 4, bodies: [($"{Root}@2", "arguments", 0), ($"{Root}@4", "text", null)]),
            Body("p3", $"{Root}@2", "call", 0),
            Body("p4", $"{Root}@4", "text", null)
        ]);

        var c = EvidenceCoverageMeasure.ForRetrieval(scope, ledger, []);

        await Assert.That(c.Omissions).IsEmpty();
        await Assert.That(c.StopReason).IsNull();
        await Assert.That(c.IsComplete).IsTrue();
        await Assert.That(EvidenceCoverageMeasure.UnopenedBodies(ledger)).IsEmpty();
    }

    [Test]
    public async Task Events_nobody_outlined_or_paged_are_reported_as_unread_ranges() {
        var scope  = Scope(Src(Root, 0, 9, 1));
        var ledger = Ledger([Outline("o1", Root, hasNext: false, (0, 0, 4, "ok")), Events("p1", Root, 0, 4)]);

        var c = EvidenceCoverageMeasure.ForRetrieval(scope, ledger, []);

        var unread = c.Omissions.Single();
        await Assert.That(unread.Kind).IsEqualTo(EvalOmissionKinds.PagesNotFetched);
        await Assert.That(unread.Detail).IsEqualTo("unread_ranges");
        await Assert.That(unread.Count).IsEqualTo(1);
        await Assert.That(c.StopReason).IsEqualTo(EvalStopReasons.JudgeStopped);
    }

    [Test]
    public async Task A_turn_whose_range_is_not_ok_is_never_counted_as_unfetched() {
        var scope  = Scope(Src(Root, 0, 4, 1));
        var ledger = Ledger([Outline("o1", Root, hasNext: false, (0, 0, 4, "unavailable")), Events("p1", Root, 0, 4)]);

        await Assert.That(Count(EvidenceCoverageMeasure.ForRetrieval(scope, ledger, []), EvalOmissionKinds.TurnsNotFetched)).IsNull();
    }

    [Test]
    public async Task A_footer_budget_stop_outranks_judge_stopped_and_trips_the_trace_coverage() {
        var scope  = Scope(Src(Root, 0, 9, 2));
        var ledger = Ledger([Outline("o1", Root, hasNext: false, (0, 0, 4, "ok"), (1, 5, 9, "ok")), Events("p1", Root, 0, 4)], Footer(EvalStopReasons.ByteBudget, delivered: 600_001, calls: 50));

        var coverage = EvidenceCoverageMeasure.ForRetrieval(scope, ledger, []);
        var trace    = EvidenceCoverageMeasure.RetrievalTraceCoverage(ledger, numTurns: 60, maxTurns: 52);

        await Assert.That(coverage.StopReason).IsEqualTo(EvalStopReasons.ByteBudget);
        await Assert.That(trace.BudgetTripped).IsTrue();
        await Assert.That(trace.DeliveredBytes).IsEqualTo(600_000);
        await Assert.That(trace.IterationsUsed).IsEqualTo(52);
        await Assert.That(trace.ToolCalls).IsEqualTo(48);
        await Assert.That(trace.Validate()).IsNull();

        var assessment = new EvalQuestionAssessment {
            Category = "safety", QuestionId = "q1", Outcome = EvalOutcomes.Assessed, Score = 4, Verdict = EvalService.VerdictForScore(4),
            Finding = "f", EvidenceCoverage = coverage, TraceCoverage = trace
        };
        await Assert.That(assessment.Validate()).IsNull();
    }

    [Test]
    public async Task A_scope_moved_footer_is_not_a_stop_reason() {
        var scope  = Scope(Src(Root, 0, 4, 1));
        var ledger = Ledger([Outline("o1", Root, hasNext: true, (0, 0, 4, "ok"))], Footer("scope_moved"));

        var c = EvidenceCoverageMeasure.ForRetrieval(scope, ledger, []);

        await Assert.That(c.StopReason).IsEqualTo(EvalStopReasons.JudgeStopped);
        await Assert.That(EvidenceCoverageMeasure.RetrievalTraceCoverage(ledger, 3, 52).BudgetTripped).IsFalse();
    }

    [Test]
    public async Task Scope_reasons_unavailable_sources_and_refusals_are_named_in_manifest_order() {
        var scope  = Scope(false, ["source_limit"], Src(Root, 0, 4, 1), Src(Lane, 0, 4, 1, available: false), Src(Lane2, 0, 4, 1));
        var ledger = Ledger([Outline("o1", Root, hasNext: false, (0, 0, 4, "ok")), Events("p1", Root, 0, 4)], Footer(null, refused: [Lane2]));

        var c = EvidenceCoverageMeasure.ForRetrieval(scope, ledger, []);

        await Assert.That(c.SourcesUnavailable).IsEquivalentTo([Lane, Lane2]);
        var reason = c.Omissions.Single(o => o.Kind == EvalOmissionKinds.ScopeIncomplete);
        await Assert.That(reason.Detail).IsEqualTo("source_limit");
        await Assert.That(reason.Count).IsEqualTo(1);
        await Assert.That(Count(c, EvalOmissionKinds.SourcesNotConsulted)).IsNull();
    }

    [Test]
    public async Task One_shot_consults_every_available_source() {
        var scope = Scope(Src(Root, 0, 4, 1), Src(Lane, 0, 4, 1, available: false));
        var cite  = new EvalEvidenceCitation { Ref = $"{Root}@1", Digest = new string('a', 64) };

        var c = EvidenceCoverageMeasure.ForOneShot(scope, [cite]);

        await Assert.That(c.SourcesConsulted).IsEquivalentTo([Root]);
        await Assert.That(c.SourcesUnavailable).IsEquivalentTo([Lane]);
        await Assert.That(c.Citations.Single()).IsEqualTo(cite);
        await Assert.That(c.StopReason).IsNull();
        await Assert.That(c.Validate()).IsNull();
    }

    [Test]
    public async Task The_iteration_cap_detail_counts_distinct_known_turns_clamped_to_the_total() {
        var scope  = Scope(Src(Root, 0, 9, 2), Src(Lane, 0, 4, null), Src(Lane2, 0, 4, 3, available: false));
        var ledger = Ledger([
            Outline("o1", Root, hasNext: false, (0, 0, 4, "ok"), (1, 5, 9, "ok")),
            Outline("p1", Root, hasNext: false, (1, 5, 9, "ok"), (5, 9, 9, "ok")),
            Outline("p2", Lane, hasNext: false, (0, 0, 4, "ok"))
        ]);

        var failure = EvidenceCoverageMeasure.IterationCap("safety", "q1", scope, ledger, maxTurns: 52);

        await Assert.That(failure.Code).IsEqualTo(EvalFailureCodes.IterationCap);
        await Assert.That(failure.MaxIterations).IsEqualTo(52);
        await Assert.That(failure.TurnsTotal).IsEqualTo(2);
        await Assert.That(failure.TurnsFetched).IsEqualTo(2);
        await Assert.That(failure.Validate()).IsNull();
    }

    static string PlanLane(int n) => $"PlanLane-{0xF000 + n:x32}-r";

    static EvidenceSourceDto LaneSource(string id, long revisions) => new() {
        SourceId = id, Kind = "plan", SessionId = "r", FirstRevision = 0, RevisionCutoff = revisions - 1, Availability = "available"
    };

    static string LedgerPlan(int n, string lane, int tasks) {
        var rows = string.Join(",", Enumerable.Range(0, tasks).Select(i =>
            $$"""{"task_id":"t{{i}}","ordinal":{{i}},"title":"task {{i}}","status":"pending","note":null,"title_ref":"{{lane}}@{{i}}","status_ref":null}"""));
        return $$"""{"plan_id":"{{0xF000 + n:x32}}","sources":["{{lane}}"],"documents":[],"tasks":[{{rows}}]}""";
    }

    static JudgeLedgerPage PlanLedger(string handle, string[] plans, int plansOmitted, int tasksOmitted) =>
        EvidencePageRenderer.Render(++_seq, handle, EvidencePageRenderer.PlanLedgerTool, """{"section":"plan_ledger"}""",
            $$"""{"scope_version":"v1","plans":[{{string.Join(",", plans)}}],"plans_total":{{plans.Length + plansOmitted}},"plans_omitted":{{plansOmitted}},"tasks_omitted":{{tasksOmitted}},"plans_unavailable":[],"plans_unavailable_omitted":0,"budget_bytes":65536,"over_budget":false}""");

    /// <summary>Every lane of a plan the plan_ledger page served is consulted, the one whose tasks the cut shortened
    /// included; a lane of a plan the cut left out still needs reading.</summary>
    [Test]
    public async Task A_lane_the_plan_ledger_served_is_consulted_and_one_it_left_out_is_not() {
        string served = PlanLane(1), shortened = PlanLane(2), omitted = PlanLane(3);
        var scope  = Scope(Src(Root, 0, 0, null), LaneSource(served, 3), LaneSource(shortened, 3), LaneSource(omitted, 3));
        var ledger = Ledger([Events("p1", Root, 0, 0), PlanLedger("o1", [LedgerPlan(1, served, 2), LedgerPlan(2, shortened, 1)], plansOmitted: 1, tasksOmitted: 5)]);

        var c = EvidenceCoverageMeasure.ForRetrieval(scope, ledger, []);

        await Assert.That(c.SourcesConsulted).IsEquivalentTo([Root, served, shortened]);
        await Assert.That(Count(c, EvalOmissionKinds.SourcesNotConsulted)).IsEqualTo(1);
        await Assert.That(c.StopReason).IsEqualTo(EvalStopReasons.JudgeStopped);
    }

    /// <summary>The fold never renders a lane's attachments or superseded entries, so a served lane with entries the page
    /// did not show is partly read, not complete.</summary>
    [Test]
    public async Task A_served_lane_with_entries_the_plan_ledger_did_not_show_reports_them_unread() {
        var lane   = PlanLane(1);
        var scope  = Scope(Src(Root, 0, 0, null), LaneSource(lane, 4));
        var ledger = Ledger([Events("p1", Root, 0, 0), PlanLedger("o1", [LedgerPlan(1, lane, 2)], plansOmitted: 0, tasksOmitted: 0)]);

        var c = EvidenceCoverageMeasure.ForRetrieval(scope, ledger, []);

        await Assert.That(c.SourcesConsulted).IsEquivalentTo([Root, lane]);
        await Assert.That(c.Omissions.Single(o => o.Kind == EvalOmissionKinds.PagesNotFetched).Detail).IsEqualTo("unread_ranges");
        await Assert.That(Count(c, EvalOmissionKinds.PagesNotFetched)).IsEqualTo(1);
        await Assert.That(c.StopReason).IsEqualTo(EvalStopReasons.JudgeStopped);
    }

    /// <summary>A ledger row shows only part of an entry, so a lane whose every ref the ledger printed is consulted but
    /// unread; the refs stay citable, and only an events page delivering the entries makes the record complete.</summary>
    [Test]
    public async Task A_lane_shown_only_through_the_plan_ledger_is_consulted_but_unread_until_its_events_are_delivered() {
        var lane   = PlanLane(1);
        var scope  = Scope(Src(Root, 0, 0, null), LaneSource(lane, 4));
        var plan   = $$"""{"plan_id":"{{0xF001:x32}}","sources":["{{lane}}"],"documents":[{"document_key":"doc","kind":"plan","path":"docs/plan.md","ref":"{{lane}}@0"}],"tasks":[{"task_id":"t1","ordinal":1,"title":"one","status":"completed","note":null,"title_ref":"{{lane}}@1","status_ref":"{{lane}}@3"},{"task_id":"t2","ordinal":2,"title":"two","status":"pending","note":null,"title_ref":"{{lane}}@2","status_ref":null}]}""";
        var shownOnly = Ledger([Events("p1", Root, 0, 0), PlanLedger("o1", [plan], plansOmitted: 0, tasksOmitted: 0)]);

        var shown = EvidenceCoverageMeasure.ForRetrieval(scope, shownOnly, []);

        await Assert.That(shown.SourcesConsulted).IsEquivalentTo([Root, lane]);
        await Assert.That(shown.Omissions.Single(o => o.Kind == EvalOmissionKinds.PagesNotFetched).Detail).IsEqualTo("unread_ranges");
        await Assert.That(shown.StopReason).IsEqualTo(EvalStopReasons.JudgeStopped);
        foreach (var r in new[] { $"{lane}@0", $"{lane}@1", $"{lane}@2", $"{lane}@3", $"{lane}@1-3" }) {
            await Assert.That(shownOnly.TryExpand(r, out _)).IsTrue();
            await Assert.That(EvidenceRefText.TryParse(r, out var parsed) && shownOnly.IsDelivered(parsed)).IsFalse();
        }

        var read = EvidenceCoverageMeasure.ForRetrieval(scope, Ledger([.. shownOnly.Pages, Events("p2", lane, 0, 3)]), []);

        await Assert.That(read.Omissions).IsEmpty();
        await Assert.That(read.StopReason).IsNull();
    }
}
