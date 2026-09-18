namespace Capacitor.Cli.Core.Eval.Contracts;

/// <summary>Daemon → server: per-question judge result on eval protocol 2. Mirrors the server's
/// <c>QuestionResultV2</c> — plain PascalCase, no <c>[JsonPropertyName]</c>, round-tripping through
/// SignalR's JSON hub protocol the same way <c>QuestionResult</c> does. Exactly one of
/// <see cref="Assessment"/> and <see cref="Failure"/> is set; <see cref="Error"/> is diagnostic text
/// that may accompany a failure.</summary>
public readonly record struct QuestionResultV2(
        EvalQuestionAssessment? Assessment,
        EvalQuestionFailure?    Failure,
        string?                 Error,
        long                    InputTokens,
        long                    OutputTokens
    );
