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
}
