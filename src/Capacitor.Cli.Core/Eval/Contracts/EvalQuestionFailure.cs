using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Eval.Contracts;

/// <summary>One question that produced no verdict. Mirrors the server's
/// <c>JudgeQuestionFailure</c> field for field. Coded, never free-text.</summary>
public record EvalQuestionFailure {
    [JsonPropertyName("category")]    public required string Category   { get; init; }
    [JsonPropertyName("question_id")] public required string QuestionId { get; init; }
    [JsonPropertyName("code")]        public required string Code       { get; init; }

    [JsonPropertyName("max_iterations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxIterations { get; init; }

    [JsonPropertyName("turns_fetched")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TurnsFetched { get; init; }

    [JsonPropertyName("turns_total")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TurnsTotal { get; init; }

    [JsonPropertyName("http_status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? HttpStatus { get; init; }

    /// <summary>The server's failure rules: iteration_cap carries its cap and turn counts, chat_error at most an HTTP status,
    /// every other code nothing.</summary>
    public string? Validate() {
        if (string.IsNullOrWhiteSpace(Category) || string.IsNullOrWhiteSpace(QuestionId)) return "category and question_id must be non-empty";
        return Code switch {
            EvalFailureCodes.IterationCap =>
                MaxIterations is null or <= 0                                                       ? "iteration_cap requires max_iterations > 0"
              : TurnsFetched is null || TurnsTotal is null || TurnsFetched < 0 || TurnsFetched > TurnsTotal ? "iteration_cap requires 0 <= turns_fetched <= turns_total"
              : HttpStatus is not null                                                              ? "iteration_cap must not carry http_status"
              : null,
            EvalFailureCodes.ChatError =>
                MaxIterations is not null || TurnsFetched is not null || TurnsTotal is not null ? "chat_error must not carry iteration fields"
              : HttpStatus is not null and (< 100 or > 599)                                     ? "http_status out of range"
              : null,
            EvalFailureCodes.JudgeTimeout or EvalFailureCodes.VerdictParseFailed or EvalFailureCodes.SpendBudget =>
                MaxIterations is not null || TurnsFetched is not null || TurnsTotal is not null || HttpStatus is not null ? $"{Code} must not carry detail fields" : null,
            _ => $"unknown code '{Code}'"
        };
    }
}
