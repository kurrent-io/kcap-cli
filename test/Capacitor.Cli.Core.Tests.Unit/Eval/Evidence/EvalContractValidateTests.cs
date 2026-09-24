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
}
