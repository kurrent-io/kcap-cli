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
}
