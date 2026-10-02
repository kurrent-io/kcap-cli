using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Eval.Contracts;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>Pins <c>kcap eval</c>'s terminal rendering of a V4 aggregate: outcome markers,
/// the indented <c>coverage:</c>/<c>cites:</c> lines, and the null-overall footer.</summary>
public class EvalCommandRenderTests {
    [Test, NotInParallel]
    public async Task Renders_mixed_outcomes_coverage_and_citations_with_unscored_footer() {
        var clean = new EvalQuestionAssessment {
            Category = "safety", QuestionId = "clean_row", Outcome = EvalOutcomes.Assessed,
            Score = 5, Verdict = "pass", Finding = "nothing to report"
        };
        var incomplete = new EvalQuestionAssessment {
            Category = "safety", QuestionId = "incomplete_row", Outcome = EvalOutcomes.Assessed,
            Score = 3, Verdict = "warn", Finding = "partial coverage",
            EvidenceCoverage = new EvalEvidenceCoverage {
                PolicyVersion = "coverage-v1",
                StopReason    = "byte_budget",
                Omissions     = [ new EvalEvidenceOmission { Kind = "tool_result_truncated", Count = 2 } ]
            }
        };
        var cited = new EvalQuestionAssessment {
            Category = "safety", QuestionId = "cited_row", Outcome = EvalOutcomes.Assessed,
            Score = 4, Verdict = "pass", Finding = "grounded in the trace",
            EvidenceCoverage = new EvalEvidenceCoverage {
                PolicyVersion = "coverage-v1",
                Citations     = [ new EvalEvidenceCitation { Ref = "AgentSession-abc@5" } ]
            }
        };
        var insufficientEvidence = new EvalQuestionAssessment {
            Category = "safety", QuestionId = "insufficient_row", Outcome = EvalOutcomes.InsufficientEvidence,
            Finding = "evidence was truncated before it reached the judge"
        };
        var notApplicable = new EvalQuestionAssessment {
            Category = "safety", QuestionId = "not_applicable_row", Outcome = EvalOutcomes.NotApplicable,
            Finding = "no code changed in this session"
        };

        var aggregate = new SessionEvalCompletedPayloadV4 {
            EvalRunId             = "run-1",
            JudgeModel            = "sonnet",
            Summary               = "test summary",
            OverallScore          = null,
            AssessedQuestions     = 0,
            UnassessedQuestions   = 2,
            JudgedQuestions       = 2,
            TotalQuestions        = 2,
            CoveragePolicyVersion = "coverage-v1",
            Categories = [
                new EvalCategoryAssessment {
                    Name    = "safety",
                    Score   = null,
                    Verdict = null,
                    Questions = [clean, incomplete, cited, insufficientEvidence, notApplicable]
                }
            ]
        };

        using var capture = ConsoleOutput.StartCapture();
        EvalCommand.Render(aggregate, "sess-1");
        var output = capture.GetCapturedOutput();
        var lines  = output.Split('\n');

        await Assert.That(output).Contains("? insufficient_row");
        await Assert.That(output).Contains("– not_applicable_row");

        // Exactly one coverage: line (the incomplete row) and one cites: line (the cited row) — a
        // clean row and an outcome-only row emit neither.
        await Assert.That(lines.Count(l => l.Contains("coverage:"))).IsEqualTo(1);
        await Assert.That(lines.Count(l => l.Contains("cites:"))).IsEqualTo(1);
        await Assert.That(output).Contains("coverage: byte_budget; tool_result_truncated×2");
        await Assert.That(output).Contains("cites: AgentSession-abc@5");

        await Assert.That(output).Contains("Overall: not scored (0/2 assessed)");
    }
    static SessionEvalCompletedPayloadV4 FailureOnly() => new() {
        EvalRunId             = "run-1",
        JudgeModel            = "sonnet",
        Summary               = "Not evaluated: all 3 questions failed (spend_budget ×2, judge_timeout ×1)",
        AssessedQuestions     = 0,
        UnassessedQuestions   = 0,
        JudgedQuestions       = 0,
        TotalQuestions        = 3,
        CoveragePolicyVersion = "coverage-v1",
        FailedQuestions = [
            new EvalQuestionFailure { Category = "safety", QuestionId = "q1", Code = "judge_timeout" },
            new EvalQuestionFailure { Category = "safety", QuestionId = "q2", Code = "spend_budget" },
            new EvalQuestionFailure { Category = "quality", QuestionId = "q3", Code = "spend_budget" }
        ]
    };

    /// <summary>A persisted failure-only run is a failed evaluation: it exits 1 and prints each code with its count,
    /// even when the baseline was written.</summary>
    [Test, NotInParallel]
    public async Task A_failure_only_result_renders_the_failed_state_with_its_codes_and_exits_one() {
        using var capture = ConsoleOutput.StartCapture();
        var exit   = EvalCommand.Report(FailureOnly(), "sess-1", baselineWriteFailed: false);
        var output = capture.GetCapturedOutput();

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(output).Contains("Evaluation failed: 3 of 3 questions not judged");
        await Assert.That(output).Contains("spend_budget ×2");
        await Assert.That(output).Contains("judge_timeout ×1");
        await Assert.That(output).Contains("✗ q3");
        await Assert.That(output).DoesNotContain("Overall:");
    }

    [Test, NotInParallel]
    public async Task A_null_result_exits_one_and_a_scored_result_exits_zero() {
        using var capture = ConsoleOutput.StartCapture();
        var scored = FailureOnly() with {
            FailedQuestions = [], TotalQuestions = 1, JudgedQuestions = 1, AssessedQuestions = 1, OverallScore = 4, Summary = "ok",
            Categories = [new EvalCategoryAssessment { Name = "safety", Score = 4, Verdict = "pass", Questions = [
                new EvalQuestionAssessment { Category = "safety", QuestionId = "q1", Outcome = EvalOutcomes.Assessed, Score = 4, Verdict = "pass", Finding = "f" }] }]
        };

        await Assert.That(EvalCommand.Report(null, "sess-1", baselineWriteFailed: false)).IsEqualTo(1);
        await Assert.That(EvalCommand.Report(scored, "sess-1", baselineWriteFailed: false)).IsEqualTo(0);
        await Assert.That(EvalCommand.Report(scored, "sess-1", baselineWriteFailed: true)).IsEqualTo(1);
    }
}
