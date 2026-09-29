namespace Capacitor.Cli.Core.Eval.Contracts;

/// <summary>Result of one <c>EvalService.RunQuestionAsync</c> invocation. Exactly one of
/// <see cref="Assessment"/> and <see cref="Failure"/> is set — never used over the wire, purely an
/// in-process return shape.</summary>
public readonly record struct QuestionRunResult(EvalQuestionAssessment? Assessment, EvalQuestionFailure? Failure, EvalUsage? Usage = null);
