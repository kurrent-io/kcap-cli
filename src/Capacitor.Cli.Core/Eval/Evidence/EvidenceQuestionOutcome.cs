using Capacitor.Cli.Core.Eval.Contracts;

namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>One evidence-route question: an assessment or a coded failure with the harness usage, or a moved scope, which
/// ends the whole run.</summary>
public sealed record EvidenceQuestionOutcome(EvalQuestionAssessment? Assessment, EvalQuestionFailure? Failure, EvalUsage? Usage, bool ScopeMoved) {
    public static EvidenceQuestionOutcome Moved { get; } = new(null, null, null, true);
}
