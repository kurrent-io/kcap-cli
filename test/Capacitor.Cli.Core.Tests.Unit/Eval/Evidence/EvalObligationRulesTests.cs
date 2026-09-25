using System.Text.Json;
using Capacitor.Cli.Core.Eval;
using Capacitor.Cli.Core.Eval.Contracts;
using Capacitor.Cli.Core.Eval.Evidence;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Core.Tests.Unit.Eval.Evidence;

/// <summary>Reconcile is total and order-free: uncertified anchors drop, anchor-equal and uncertified citations go, a decisive
/// status without evidence becomes unverified, equal ids merge, and the outcome moves only for an assessed checklist with no
/// decisive entry. DeriveId ignores whitespace runs and depends only on the anchor ref and title. The server's derive-id and
/// reconcile vectors hold.</summary>
public class EvalObligationRulesTests {
    const string Root = "AgentSession-r";
    static readonly string Digest = new('a', 64);

    static EvalEvidenceCitation C(long revision) => new() { Ref = $"{Root}@{revision}", Digest = Digest };

    static readonly IReadOnlyDictionary<string, EvalEvidenceCitation> Certified = new Dictionary<string, EvalEvidenceCitation> {
        ["p1.1"] = C(1), ["p1.2"] = C(2), ["p1.3"] = C(3), ["p1.4"] = C(4), ["p1.5"] = C(5), ["p1.6"] = C(6)
    };

    static EvalQuestionAssessment Assessed() => new() {
        Category = "plan_adherence", QuestionId = "completed_items", Outcome = EvalOutcomes.Assessed, Score = 4, Verdict = "pass", Finding = "f"
    };

    static EvalReportedObligation O(string title, string status, string anchor, params string[] citations) => new(title, "plan", status, anchor, citations, null);

    static string Json(EvalQuestionAssessment a) => JsonSerializer.Serialize(a, CapacitorJsonContext.Default.EvalQuestionAssessment);

    static JsonDocument Vector(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "eval-strategies", name)));

    [Test]
    public async Task Derive_id_ignores_whitespace_runs_and_depends_on_the_anchor_and_the_title() {
        await Assert.That(EvalObligationRules.DeriveId($"{Root}@3", "  Write   the\ttests ")).IsEqualTo(EvalObligationRules.DeriveId($"{Root}@3", "Write the tests"));
        await Assert.That(EvalObligationRules.DeriveId($"{Root}@4", "Write the tests")).IsNotEqualTo(EvalObligationRules.DeriveId($"{Root}@3", "Write the tests"));
        await Assert.That(EvalObligationRules.Normalize("  Write   the\ttests ")).IsEqualTo("Write the tests");
    }

    [Test]
    public async Task Every_server_derive_id_vector_holds() {
        using var vectors = Vector("derive-id.json");

        foreach (var v in vectors.RootElement.EnumerateArray())
            await Assert.That(EvalObligationRules.DeriveId(v.GetProperty("anchor").GetString()!, v.GetProperty("title").GetString()!)).IsEqualTo(v.GetProperty("id").GetString());
    }

    [Test]
    public async Task Every_server_reconcile_vector_holds() {
        using var vectors = Vector("reconcile.json");
        var source = vectors.RootElement.GetProperty("source").GetString()!;
        var digest = vectors.RootElement.GetProperty("digest").GetString()!;

        foreach (var v in vectors.RootElement.GetProperty("cases").EnumerateArray()) {
            var name     = v.GetProperty("name").GetString()!;
            var outcome  = v.GetProperty("outcome").GetString()!;
            var reported = EvalObligationContract.TryParse(v.GetProperty("reported"))!;
            var certified = v.GetProperty("certified").EnumerateArray().Select(t => t.GetString()!)
                .ToDictionary(t => t, t => new EvalEvidenceCitation { Ref = $"{source}@{t[1..]}", Digest = digest });
            var assessment = outcome == EvalOutcomes.Assessed ? Assessed() : Assessed() with { Outcome = outcome, Score = null, Verdict = null };

            var result = EvalObligationRules.Reconcile(assessment, reported, certified, v.GetProperty("is_reporting").GetBoolean());

            await Assert.That(result.Outcome).IsEqualTo(v.GetProperty("expected_outcome").GetString()).Because(name);
            var expected = v.GetProperty("expected");
            if (expected.ValueKind == JsonValueKind.Null) {
                await Assert.That(result.Obligations).IsNull().Because(name);
                continue;
            }
            var actual = result.Obligations!;
            await Assert.That(actual.Count).IsEqualTo(expected.GetArrayLength()).Because(name);
            var i = 0;
            foreach (var e in expected.EnumerateArray()) {
                var o = actual[i++];
                await Assert.That(o.Id).IsEqualTo(e.GetProperty("id").GetString()).Because(name);
                await Assert.That(o.Title).IsEqualTo(e.GetProperty("title").GetString()).Because(name);
                await Assert.That(o.Origin).IsEqualTo(e.GetProperty("origin").GetString()).Because(name);
                await Assert.That(o.Status).IsEqualTo(e.GetProperty("status").GetString()).Because(name);
                await Assert.That(o.Anchor.Ref).IsEqualTo(e.GetProperty("anchor").GetString()).Because(name);
                await Assert.That(o.Note).IsEqualTo(e.GetProperty("note").GetString()).Because(name);
                await Assert.That(o.Citations.Select(c => c.Ref)).IsEquivalentTo(e.GetProperty("citations").EnumerateArray().Select(c => c.GetString()!), CollectionOrdering.Matching).Because(name);
            }
            await Assert.That(result.Validate()).IsNull().Because(name);
        }
    }

    [Test]
    public async Task A_question_that_does_not_report_or_does_not_apply_keeps_no_obligations() {
        var reported = new[] { O("t", "verified", "p1.1", "p1.2") };

        await Assert.That(EvalObligationRules.Reconcile(Assessed(), reported, Certified, isReportingQuestion: false).Obligations).IsNull();
        var notApplicable = Assessed() with { Outcome = EvalOutcomes.NotApplicable, Score = null, Verdict = null };
        await Assert.That(EvalObligationRules.Reconcile(notApplicable, reported, Certified, true)).IsEqualTo(notApplicable with { Obligations = null });
        await Assert.That(EvalObligationRules.Reconcile(Assessed(), [], Certified, true).Obligations).IsNull();
        await Assert.That(EvalObligationRules.Reconcile(Assessed(), null, Certified, true).Obligations).IsNull();
    }

    [Test]
    public async Task An_uncertified_anchor_drops_the_entry_whatever_its_status() {
        var result = EvalObligationRules.Reconcile(Assessed(), [O("kept", "verified", "p1.1", "p1.2"), O("dropped", "verified", "p9.9", "p1.2")], Certified, true);

        await Assert.That(result.Obligations!.Select(o => o.Title)).IsEquivalentTo(["kept"]);
    }

    [Test]
    public async Task A_certified_token_without_a_digest_is_uncertified() {
        var noDigest = new Dictionary<string, EvalEvidenceCitation>(Certified) { ["p1.1"] = new() { Ref = $"{Root}@1" } };

        await Assert.That(EvalObligationRules.Reconcile(Assessed(), [O("a", "verified", "p1.1", "p1.2")], noDigest, true).Obligations).IsNull();
    }

    [Test]
    public async Task Citations_equal_to_the_anchor_or_uncertified_go_and_a_decisive_status_without_one_is_unverified() {
        var result = EvalObligationRules.Reconcile(Assessed(), [O("a", "verified", "p1.1", "p1.2"), O("b", "not_done", "p1.3", "p1.3", "p9.9")], Certified, true);

        var b = result.Obligations!.Single(o => o.Title == "b");
        await Assert.That(b.Status).IsEqualTo(EvalObligationContract.Unverified);
        await Assert.That(b.Citations).IsEmpty();
        await Assert.That(result.Obligations!.Single(o => o.Title == "a").Citations.Single()).IsEqualTo(C(2));
    }

    [Test]
    public async Task Every_permutation_yields_identical_output_and_equal_ids_with_differing_statuses_merge_to_unverified() {
        var entries = new[] { O("Ship it", "verified", "p1.1", "p1.2", "p1.3"), O("Ship  it", "claimed", "p1.1", "p1.4"), O("Other", "verified", "p1.5", "p1.6") };
        var first   = Json(EvalObligationRules.Reconcile(Assessed(), entries, Certified, true));

        foreach (var order in new[] { new[] { 2, 1, 0 }, new[] { 1, 0, 2 }, new[] { 1, 2, 0 } }) {
            var permuted = order.Select(i => entries[i] with { Citations = [.. entries[i].Citations.Reverse()] }).ToArray();
            await Assert.That(Json(EvalObligationRules.Reconcile(Assessed(), permuted, Certified, true))).IsEqualTo(first);
        }

        var merged = EvalObligationRules.Reconcile(Assessed(), entries, Certified, true).Obligations!.Single(o => o.Title == "Ship it");
        await Assert.That(merged.Status).IsEqualTo(EvalObligationContract.Unverified);
        await Assert.That(merged.Citations.Select(c => c.Ref)).IsEquivalentTo([$"{Root}@2", $"{Root}@3", $"{Root}@4"]);
    }

    [Test]
    public async Task An_assessed_checklist_with_no_decisive_entry_becomes_insufficient_and_validates_once_coverage_is_reconciled() {
        var complete = new EvalEvidenceCoverage { PolicyVersion = "coverage-v2", ScopeVersion = "v1", SourcesConsulted = [Root] };
        var result = EvalObligationRules.Reconcile(Assessed() with { EvidenceCoverage = complete }, [O("a", "unverified", "p1.1")], Certified, true);

        await Assert.That(result.Outcome).IsEqualTo(EvalOutcomes.InsufficientEvidence);
        await Assert.That(result.Score).IsNull();
        await Assert.That(result.Verdict).IsNull();
        await Assert.That(result.Validate()).IsNotNull();
        var reconciled = result with { EvidenceCoverage = EvalService.ReconcileEvidenceCoverage(result.Outcome, result.EvidenceCoverage) };
        await Assert.That(reconciled.EvidenceCoverage).IsNull();
        await Assert.That(reconciled.Validate()).IsNull();
    }

    [Test]
    public async Task An_insufficient_checklist_keeps_its_entries_and_its_outcome() {
        var insufficient = Assessed() with { Outcome = EvalOutcomes.InsufficientEvidence, Score = null, Verdict = null };

        var result = EvalObligationRules.Reconcile(insufficient, [O("a", "verified", "p1.1", "p1.2")], Certified, true);

        await Assert.That(result.Outcome).IsEqualTo(EvalOutcomes.InsufficientEvidence);
        await Assert.That(result.Obligations!.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Ten_anchored_tasks_six_proven_yield_ten_records_four_unverified_and_stay_assessed() {
        var certified = Enumerable.Range(1, 20).ToDictionary(i => $"p2.{i}", i => C(100 + i));
        var reported  = Enumerable.Range(1, 10).Select(i => new EvalReportedObligation($"task {i}", "plan", "verified", $"p2.{i}", i <= 6 ? [$"p2.{i + 10}"] : [], null)).ToArray();

        var result = EvalObligationRules.Reconcile(Assessed(), reported, certified, true);

        await Assert.That(result.Obligations!.Count).IsEqualTo(10);
        await Assert.That(result.Obligations!.Count(o => o.Status == EvalObligationContract.Unverified)).IsEqualTo(4);
        await Assert.That(result.Outcome).IsEqualTo(EvalOutcomes.Assessed);
        await Assert.That(result.Validate()).IsNull();
    }
}
