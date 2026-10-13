using Capacitor.Cli.Core.Eval;
using Capacitor.Cli.Core.Eval.Contracts;

namespace Capacitor.Cli.Core.Tests.Unit.Eval.Evidence;

/// <summary>The CLI's mirrors of the server's validation reject what the V4 route would reject, so a bad question
/// becomes one coded failure instead of a 400 that loses the whole run.</summary>
public class EvalContractValidateTests {
    static EvalQuestionFailure Failure(string code) => new() { Category = "c", QuestionId = "q", Code = code };

    static EvalQuestionAssessment Assessed(EvalEvidenceCoverage? coverage = null, EvalTraceCoverage? trace = null) => new() {
        Category = "c", QuestionId = "q", Outcome = EvalOutcomes.Assessed, Score = 4, Verdict = "pass", Finding = "f",
        EvidenceCoverage = coverage, TraceCoverage = trace
    };

    static EvalEvidenceCoverage V2(string? stop = null, params EvalEvidenceOmission[] omissions) => new() {
        PolicyVersion = EvalService.EvidenceCoveragePolicyVersion, SourcesConsulted = ["AgentSession-r"], Omissions = [.. omissions], StopReason = stop
    };

    [Test]
    public async Task Iteration_cap_needs_its_three_fields_in_range_and_spend_budget_needs_none() {
        await Assert.That((Failure(EvalFailureCodes.IterationCap) with { MaxIterations = 52, TurnsFetched = 3, TurnsTotal = 10 }).Validate()).IsNull();
        await Assert.That((Failure(EvalFailureCodes.IterationCap) with { MaxIterations = 52, TurnsFetched = 11, TurnsTotal = 10 }).Validate()).IsNotNull();
        await Assert.That((Failure(EvalFailureCodes.IterationCap) with { MaxIterations = 0, TurnsFetched = 0, TurnsTotal = 0 }).Validate()).IsNotNull();
        await Assert.That(Failure(EvalFailureCodes.SpendBudget).Validate()).IsNull();
        await Assert.That((Failure(EvalFailureCodes.SpendBudget) with { HttpStatus = 400 }).Validate()).IsNotNull();
        await Assert.That(Failure("fan_in_oops").Validate()).IsNotNull();
    }

    [Test]
    public async Task Retrieval_trace_coverage_validates_its_fields_and_its_budget_stop() {
        var tripped = EvalTraceCoverage.ForEvidenceRetrieval(budgetTripped: true, deliveredBytes: 10, budgetBytes: 600_000, iterationsUsed: 7, maxIterations: 52, toolCalls: 48, maxToolCalls: 48);
        await Assert.That(Assessed(V2(EvalStopReasons.ToolCallBudget, new EvalEvidenceOmission { Kind = EvalOmissionKinds.PagesNotFetched, Count = 1 }), tripped).Validate()).IsNull();
        await Assert.That(Assessed(V2(EvalStopReasons.JudgeStopped, new EvalEvidenceOmission { Kind = EvalOmissionKinds.PagesNotFetched, Count = 1 }), tripped).Validate()).IsNotNull();
        await Assert.That((tripped with { ToolCalls = 49 }).Validate()).IsNotNull();
        await Assert.That((tripped with { TurnsFetched = 1 }).Validate()).IsNotNull();
        await Assert.That(Assessed(V2() with { PolicyVersion = EvalService.CoveragePolicyVersion }, tripped with { BudgetTripped = false }).Validate()).IsNotNull();
    }

    [Test]
    public async Task One_shot_trace_coverage_carries_only_its_own_fields() {
        await Assert.That(EvalTraceCoverage.ForOneShot(false, 1_000, 1_000, 400_000).Validate()).IsNull();
        await Assert.That((EvalTraceCoverage.ForOneShot(false, 1_000, 1_000, 400_000) with { ToolCalls = 1 }).Validate()).IsNotNull();
    }

    [Test]
    public async Task The_coverage_vocabulary_follows_the_policy_version_and_digests_are_lower_hex() {
        var scopeIncomplete = new EvalEvidenceOmission { Kind = EvalOmissionKinds.ScopeIncomplete, Count = 1, Detail = "source_cap" };
        await Assert.That(V2(null, scopeIncomplete).Validate()).IsNull();
        await Assert.That((V2(null, scopeIncomplete) with { PolicyVersion = EvalService.CoveragePolicyVersion }).Validate()).IsNotNull();
        await Assert.That((V2() with { Citations = [new EvalEvidenceCitation { Ref = "AgentSession-r@1", Digest = new string('a', 64) }] }).Validate()).IsNull();
        await Assert.That((V2() with { Citations = [new EvalEvidenceCitation { Ref = "AgentSession-r@1", Digest = new string('A', 64) }] }).Validate()).IsNotNull();
        await Assert.That((V2() with { Citations = [.. Enumerable.Range(0, 201).Select(i => new EvalEvidenceCitation { Ref = $"AgentSession-r@{i}" })] }).Validate()).IsNotNull();
    }

    [Test]
    public async Task Insufficient_evidence_cannot_sit_beside_a_complete_record() {
        var a = new EvalQuestionAssessment { Category = "c", QuestionId = "q", Outcome = EvalOutcomes.InsufficientEvidence, Finding = "f", EvidenceCoverage = V2() };
        await Assert.That(a.Validate()).IsNotNull();
        await Assert.That((a with { EvidenceCoverage = V2(EvalStopReasons.TimeBudget) }).Validate()).IsNull();
    }

    [Test]
    public async Task Strategy_stamps_go_together_in_their_form_and_every_obligation_rule_is_the_servers() {
        await Assert.That((Assessed() with { Strategy = "completion", StrategyVersion = "completion-v1" }).Validate()).IsNull();
        await Assert.That((Assessed() with { Strategy = "completion" }).Validate()).IsNotNull();
        await Assert.That((Assessed() with { Strategy = "Completion", StrategyVersion = "completion-v1" }).Validate()).IsNotNull();

        var digest = new string('d', 64);
        var ok = new EvalObligationResult {
            Id = Capacitor.Cli.Core.Eval.Evidence.EvalObligationRules.DeriveId("AgentSession-r@1", "Ship it"), Title = "Ship it", Origin = "plan", Status = "verified",
            Anchor = new() { Ref = "AgentSession-r@1", Digest = digest }, Citations = [new() { Ref = "AgentSession-r@2", Digest = digest }]
        };
        await Assert.That((Assessed() with { Obligations = [ok] }).Validate()).IsNull();
        await Assert.That((Assessed() with { Obligations = [] }).Validate()).IsNotNull();
        await Assert.That((Assessed() with { Obligations = [ok, ok] }).Validate()).IsNotNull();
        await Assert.That((Assessed() with { Obligations = [ok with { Id = "ob:0000000000000000" }] }).Validate()).IsNotNull();
        await Assert.That((Assessed() with { Obligations = [ok with { Citations = [] }] }).Validate()).IsNotNull();
        await Assert.That((Assessed() with { Obligations = [ok with { Citations = [ok.Anchor] }] }).Validate()).IsNotNull();
        await Assert.That((Assessed() with { Obligations = [ok with { Anchor = ok.Anchor with { Digest = null } }] }).Validate()).IsNotNull();
        await Assert.That((Assessed() with { Obligations = [ok with { Status = "unverified", Citations = [] }] }).Validate()).IsNotNull();
        var notApplicable = new EvalQuestionAssessment { Category = "c", QuestionId = "q", Outcome = EvalOutcomes.NotApplicable, Finding = "f", Obligations = [ok] };
        await Assert.That(notApplicable.Validate()).IsNotNull();
    }

    static EvalEvidenceCoverage V3(params EvalEvidenceOmission[] omissions) => new() {
        PolicyVersion = EvalService.ExtractCoveragePolicyVersion, SourcesConsulted = ["AgentSession-r"], Omissions = [.. omissions]
    };

    static readonly EvalEvidenceOmission Elided = new() { Kind = EvalOmissionKinds.FieldElided, Count = 1, Detail = "result_excerpt" };

    [Test]
    public async Task An_unknown_newer_policy_admits_the_v3_omission_kinds() {
        var chunks    = new EvalEvidenceOmission { Kind = EvalOmissionKinds.ChunksNotExtracted, Count = 1 };
        var delivered = new EvalEvidenceOmission { Kind = EvalOmissionKinds.RecordsNotDelivered, Count = 1 };
        var newer     = V3(chunks, delivered) with { PolicyVersion = "coverage-v4" };
        await Assert.That(newer.Validate()).IsNull();
    }

    [Test]
    public async Task An_extract_trace_without_an_evidence_coverage_record_is_refused() {
        var bare = Assessed(null, EvalTraceCoverage.ForEvidenceExtract(1, 1, 1, 1));
        await Assert.That(bare.Validate()).IsNotNull();
    }

    [Test]
    public async Task The_extract_mode_validates_its_eight_fields_and_refuses_other_modes_fields() {
        var extract = EvalTraceCoverage.ForEvidenceExtract(4, 3, 10, 7, sliceCount: 3, slicesJudged: 2, prescreenCandidates: 5, evidenceIncomplete: true);
        await Assert.That(extract.Validate()).IsNull();
        await Assert.That((extract with { ChunksExtracted = 5 }).Validate()).IsNotNull();
        await Assert.That((extract with { RecordsDelivered = 11 }).Validate()).IsNotNull();
        await Assert.That((extract with { ChunkCount = null }).Validate()).IsNotNull();
        await Assert.That((extract with { ToolCalls = 1 }).Validate()).IsNotNull();
        await Assert.That((extract with { SliceCount = 1, SlicesJudged = 1 }).Validate()).IsNotNull();
        await Assert.That((extract with { SlicesJudged = null }).Validate()).IsNotNull();
        await Assert.That((extract with { SlicesJudged = 4 }).Validate()).IsNotNull();
        await Assert.That((extract with { PrescreenCandidates = -1 }).Validate()).IsNotNull();
        await Assert.That((EvalTraceCoverage.ForOneShot(false, 1, 1, 400_000) with { ChunkCount = 1 }).Validate()).IsNotNull();
        await Assert.That((EvalTraceCoverage.ForOneShot(false, 1, 1, 400_000) with { EvidenceIncomplete = false }).Validate()).IsNotNull();
    }

    [Test]
    public async Task Extract_is_partial_on_an_unextracted_chunk_an_undelivered_record_an_unjudged_slice_or_incomplete_evidence() {
        await Assert.That(EvalTraceCoverage.ForEvidenceExtract(4, 4, 10, 10).IsPartial).IsFalse();
        await Assert.That(EvalTraceCoverage.ForEvidenceExtract(4, 3, 10, 10).IsPartial).IsTrue();
        await Assert.That(EvalTraceCoverage.ForEvidenceExtract(4, 4, 10, 9).IsPartial).IsTrue();
        await Assert.That(EvalTraceCoverage.ForEvidenceExtract(4, 4, 10, 10, sliceCount: 3, slicesJudged: 2).IsPartial).IsTrue();
        await Assert.That(EvalTraceCoverage.ForEvidenceExtract(4, 4, 10, 10, evidenceIncomplete: true).IsPartial).IsTrue();
    }

    [Test]
    public async Task The_two_extract_kinds_belong_to_coverage_v3_and_mode_and_policy_pair_both_ways() {
        var chunks = new EvalEvidenceOmission { Kind = EvalOmissionKinds.ChunksNotExtracted, Count = 2, Detail = "chunks 2-3" };
        await Assert.That(V3(chunks).Validate()).IsNull();
        await Assert.That(V2(null, chunks).Validate()).IsNotNull();

        var extract = EvalTraceCoverage.ForEvidenceExtract(1, 1, 1, 1);
        await Assert.That(Assessed(V3(), extract).Validate()).IsNull();
        await Assert.That(Assessed(V2(), extract).Validate()).IsNotNull();
        await Assert.That(Assessed(V3(), EvalTraceCoverage.ForOneShot(false, 1, 1, 400_000)).Validate()).IsNotNull();
        var abstained = Assessed(V3(), extract) with { Outcome = EvalOutcomes.InsufficientEvidence, Score = null, Verdict = null };
        await Assert.That(abstained.Validate()).IsNull();
    }

    [Test]
    public async Task Evidence_incomplete_agrees_with_the_record_and_no_judged_slice_needs_insufficient_evidence() {
        await Assert.That(Assessed(V3(), EvalTraceCoverage.ForEvidenceExtract(1, 1, 1, 1, evidenceIncomplete: true)).Validate()).IsNotNull();
        await Assert.That(Assessed(V3(Elided), EvalTraceCoverage.ForEvidenceExtract(1, 1, 1, 1, evidenceIncomplete: true)).Validate()).IsNull();

        var none = EvalTraceCoverage.ForEvidenceExtract(1, 1, 1, 1, sliceCount: 2, slicesJudged: 0, evidenceIncomplete: true);
        await Assert.That(Assessed(V3(Elided), none).Validate()).IsNotNull();
        await Assert.That((Assessed(V3(Elided), none) with { Outcome = EvalOutcomes.InsufficientEvidence, Score = null, Verdict = null }).Validate()).IsNull();
    }

    [Test]
    public async Task Decided_by_follows_the_servers_rules() {
        var trace = EvalTraceCoverage.ForEvidenceExtract(1, 1, 1, 1, prescreenCandidates: 0, evidenceIncomplete: false);
        var pass  = Assessed(V3(), trace) with { Score = 5, Verdict = "pass", DecidedBy = EvalQuestionAssessment.DecidedByPrescreen, PrescreenRulesVersion = 1 };
        await Assert.That(pass.Validate()).IsNull();
        await Assert.That((pass with { Outcome = EvalOutcomes.NotApplicable, Score = null, Verdict = null }).Validate()).IsNull();

        await Assert.That((pass with { PrescreenRulesVersion = null }).Validate()).IsNotNull();
        await Assert.That((pass with { DecidedBy = "judge" }).Validate()).IsNotNull();
        await Assert.That((pass with { Score = 4 }).Validate()).IsNotNull();
        await Assert.That((pass with { TraceCoverage = trace with { PrescreenCandidates = 2 } }).Validate()).IsNotNull();
        await Assert.That((pass with { TraceCoverage = trace with { SliceCount = 2, SlicesJudged = 2 } }).Validate()).IsNotNull();
        await Assert.That((pass with { EvidenceCoverage = V3(Elided), TraceCoverage = trace with { EvidenceIncomplete = true } }).Validate()).IsNotNull();
        await Assert.That((pass with { EvidenceCoverage = V2(), TraceCoverage = EvalTraceCoverage.ForOneShot(false, 1, 1, 400_000) }).Validate()).IsNotNull();
    }

    [Test]
    public async Task The_omission_sets_nest_and_all_is_the_full_vocabulary_an_unknown_version_gets() {
        await Assert.That(EvalOmissionKinds.V1.Contains(EvalOmissionKinds.ScopeIncomplete)).IsFalse();
        await Assert.That(EvalOmissionKinds.V2.Contains(EvalOmissionKinds.ScopeIncomplete)).IsTrue();
        await Assert.That(EvalOmissionKinds.V2.Contains(EvalOmissionKinds.ChunksNotExtracted)).IsFalse();
        await Assert.That(EvalOmissionKinds.V2.Contains(EvalOmissionKinds.RecordsNotDelivered)).IsFalse();
        await Assert.That(EvalOmissionKinds.All.IsSupersetOf(EvalOmissionKinds.V2)).IsTrue();
        await Assert.That(EvalOmissionKinds.All.Contains(EvalOmissionKinds.ChunksNotExtracted)).IsTrue();
        await Assert.That(EvalOmissionKinds.All.Contains(EvalOmissionKinds.RecordsNotDelivered)).IsTrue();
        await Assert.That(EvalOmissionKinds.All.Count).IsEqualTo(15);
    }

    [Test]
    public async Task A_sliced_extract_may_stop_on_a_time_or_token_budget_and_no_other_stop_than_the_known_ones() {
        var notJudged = new EvalEvidenceOmission { Kind = EvalOmissionKinds.RecordsNotDelivered, Count = 4, Detail = "not_judged" };
        var trace     = EvalTraceCoverage.ForEvidenceExtract(2, 2, 9, 9, sliceCount: 3, slicesJudged: 1, prescreenCandidates: 2, evidenceIncomplete: true);

        foreach (var stop in new[] { EvalStopReasons.TimeBudget, EvalStopReasons.TokenBudget }) {
            var sliced = Assessed(V3(notJudged) with { StopReason = stop }, trace);
            await Assert.That(sliced.Validate()).IsNull();
            await Assert.That((sliced with { Outcome = EvalOutcomes.InsufficientEvidence, Score = null, Verdict = null }).Validate()).IsNull();
        }

        await Assert.That(Assessed(V3(notJudged) with { StopReason = "wall_clock" }, trace).Validate()).IsNotNull();
        await Assert.That(Assessed(V3(notJudged) with { StopReason = EvalStopReasons.TimeBudget }, trace with { EvidenceIncomplete = false }).Validate()).IsNotNull();
    }

    [Test]
    public async Task A_prescreen_decision_is_valid_under_any_rules_version_from_one() {
        var trace = EvalTraceCoverage.ForEvidenceExtract(1, 1, 1, 1, prescreenCandidates: 0, evidenceIncomplete: false);
        var pass  = Assessed(V3(), trace) with { Score = 5, Verdict = "pass", DecidedBy = EvalQuestionAssessment.DecidedByPrescreen, PrescreenRulesVersion = 2 };
        await Assert.That(pass.Validate()).IsNull();
        await Assert.That((pass with { PrescreenRulesVersion = 7 }).Validate()).IsNull();
        await Assert.That((pass with { PrescreenRulesVersion = 0 }).Validate()).IsNotNull();
    }

    [Test]
    public async Task A_null_citation_or_omission_is_refused_not_thrown_on() {
        await Assert.That((V2() with { Citations = [null!] }).Validate()).IsNotNull();
        await Assert.That((V2() with { Omissions = [null!] }).Validate()).IsNotNull();
    }

    [Test]
    public async Task One_shot_trace_coverage_refuses_a_negative_count_and_a_non_positive_limit() {
        var ok = EvalTraceCoverage.ForOneShot(false, 0, 0, 1);
        await Assert.That(ok.Validate()).IsNull();
        await Assert.That((ok with { TraceChars = -1 }).Validate()).IsNotNull();
        await Assert.That((ok with { TraceTotalChars = -1 }).Validate()).IsNotNull();
        await Assert.That((ok with { TraceLimitChars = 0 }).Validate()).IsNotNull();
    }
}
