using System.Text.Json;
using Capacitor.Cli.Core.Eval;
using Capacitor.Cli.Core.Eval.Contracts;

namespace Capacitor.Cli.Core.Tests.Unit.Eval;

/// <summary>Pins the V4 aggregate: overall and category scores are the mean of assessed question
/// scores only, never a mean of category means, and null when nothing was assessed.</summary>
public class EvalServiceAggregateV4Tests {
    static readonly IReadOnlyList<EvalQuestionDto> Questions = [
        new() { Category = "safety",  Id = "a", Text = "t", Prompt = "p" },
        new() { Category = "safety",  Id = "b", Text = "t", Prompt = "p" },
        new() { Category = "safety",  Id = "c", Text = "t", Prompt = "p" },
        new() { Category = "quality", Id = "d", Text = "t", Prompt = "p" },
    ];

    static EvalQuestionAssessment A(string category, string id, int score) => new() {
        Category = category, QuestionId = id, Outcome = EvalOutcomes.Assessed,
        Score    = score, Verdict = EvalService.VerdictForScore(score), Finding = "f"
    };

    static EvalQuestionAssessment U(string category, string id) => new() {
        Category = category, QuestionId = id, Outcome = EvalOutcomes.InsufficientEvidence,
        Score    = null, Verdict = null, Finding = "insufficient evidence"
    };

    [Test]
    public async Task Overall_is_the_rounded_mean_of_assessed_question_scores_not_of_category_means() {
        // safety: 5, 5, 5 ; quality: 1  → question mean 4.0 → 4 ; category-mean formula would give (5+1)/2 = 3
        var a = EvalService.Aggregate([A("safety", "a", 5), A("safety", "b", 5), A("safety", "c", 5), A("quality", "d", 1)], [], "run", "m", Questions);

        await Assert.That(a.OverallScore).IsEqualTo(4);
        await Assert.That(a.Categories.Single(c => c.Name == "quality").Score).IsEqualTo(1);
    }

    [Test]
    public async Task All_unassessed_gives_null_overall_and_null_category_scores() {
        var a = EvalService.Aggregate([U("safety", "a"), U("quality", "d")], [], "run", "m", Questions);

        await Assert.That(a.OverallScore).IsNull();
        await Assert.That(a.Categories.All(c => c.Score is null && c.Verdict is null)).IsTrue();
        await Assert.That(a.AssessedQuestions).IsEqualTo(0);
        await Assert.That(a.UnassessedQuestions).IsEqualTo(2);
    }

    [Test]
    public async Task Failures_populate_the_counts() {
        var a = EvalService.Aggregate([A("safety", "a", 4)], [new EvalQuestionFailure { Category = "quality", QuestionId = "d", Code = "judge_timeout" }], "run", "m", Questions);

        await Assert.That(a.JudgedQuestions).IsEqualTo(1);
        await Assert.That(a.TotalQuestions).IsEqualTo(2);
        await Assert.That(a.FailedQuestions.Single().Code).IsEqualTo("judge_timeout");
        await Assert.That(a.CoveragePolicyVersion).IsEqualTo("coverage-v1");
    }

    [Test]
    public async Task Mixed_assessed_and_unassessed_counts_both_and_stamps_prompt_version() {
        var versioned = Questions.Select(q => q.Id == "a" ? q with { PromptVersion = "17" } : q).ToList();

        var a = EvalService.Aggregate([A("safety", "a", 5), U("safety", "b")], [], "run", "m", versioned);

        await Assert.That(a.AssessedQuestions).IsEqualTo(1);
        await Assert.That(a.UnassessedQuestions).IsEqualTo(1);
        await Assert.That(a.JudgedQuestions).IsEqualTo(2);
        var safety = a.Categories.Single(c => c.Name == "safety");
        await Assert.That(safety.Questions.Single(q => q.QuestionId == "a").PromptVersion).IsEqualTo("17");
    }
    static EvalQuestionFailure F(string id, string code) => new() { Category = "safety", QuestionId = id, Code = code };

    [Test]
    public async Task All_failed_is_the_failure_only_shape_with_its_codes_counted_from_the_data() {
        var failures = new[] { F("a", "judge_timeout"), F("b", "spend_budget"), F("c", "spend_budget") };

        var a = EvalService.Aggregate([], failures, "run", "m", Questions);

        await Assert.That(a.IsFailureOnly).IsTrue();
        await Assert.That(a.Categories).IsEmpty();
        await Assert.That(a.OverallScore).IsNull();
        await Assert.That(a.AssessedQuestions).IsEqualTo(0);
        await Assert.That(a.UnassessedQuestions).IsEqualTo(0);
        await Assert.That(a.JudgedQuestions).IsEqualTo(0);
        await Assert.That(a.TotalQuestions).IsEqualTo(3);
        await Assert.That(a.Retrospective).IsNull();
        await Assert.That(a.RetrospectivePromptVersion).IsNull();
        await Assert.That(a.FactsUsed).IsEmpty();
        await Assert.That(a.Summary).IsEqualTo("Not evaluated: all 3 questions failed (spend_budget ×2, judge_timeout ×1)");
    }

    [Test]
    public async Task A_mixed_or_all_unassessed_run_is_not_failure_only_and_keeps_its_summary() {
        var mixed      = EvalService.Aggregate([A("safety", "a", 4)], [F("b", "judge_timeout")], "run", "m", Questions);
        var unassessed = EvalService.Aggregate([U("safety", "a")], [], "run", "m", Questions);

        await Assert.That(mixed.IsFailureOnly).IsFalse();
        await Assert.That(mixed.Summary).IsEqualTo("Evaluated 1/2 questions across 1 categories. 1 assessed, 0 not assessed. Overall: 4/5 (pass).");
        await Assert.That(unassessed.IsFailureOnly).IsFalse();
        await Assert.That(unassessed.Summary).IsEqualTo("Evaluated 1/1 questions across 1 categories. 0 assessed, 1 not assessed. Overall: not scored.");
    }

    [Test]
    public async Task IsFailureOnly_never_reaches_the_wire() {
        var ordinary    = EvalService.Aggregate([A("safety", "a", 4)], [F("b", "judge_timeout")], "run", "m", Questions);
        var failureOnly = EvalService.Aggregate([], [F("b", "judge_timeout")], "run", "m", Questions);
        string[] wire = [
            "eval_run_id", "judge_model", "categories", "overall_score", "summary", "retrospective", "retrospective_prompt_version", "facts_used",
            "assessed_questions", "unassessed_questions", "judged_questions", "total_questions", "failed_questions", "coverage_policy_version",
            "evidence_scope_version"
        ];

        foreach (var payload in new[] { ordinary, failureOnly }) {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(payload, CapacitorJsonContext.Default.SessionEvalCompletedPayloadV4));
            await Assert.That(doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray()).IsEquivalentTo(wire);
        }
    }
}
