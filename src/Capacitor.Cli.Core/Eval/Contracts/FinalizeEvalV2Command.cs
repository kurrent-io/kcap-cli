namespace Capacitor.Cli.Core.Eval.Contracts;

/// <summary>Server → daemon: finalize an eval protocol 2 run with the assessments and coded
/// failures the orchestrator collected, rather than a single verdict list. Mirrors the server's
/// <c>FinalizeEvalV2Command</c>.</summary>
public readonly record struct FinalizeEvalV2Command(
        string                                 EvalRunId,
        IReadOnlyList<EvalQuestionAssessment>  Assessments,
        IReadOnlyList<EvalQuestionFailure>     Failures,
        string                                 Model
    );
