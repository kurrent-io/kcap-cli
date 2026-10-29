using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Eval;

/// <summary>A question that failed before producing a verdict, recorded in a <c>--baseline-out</c>
/// file so a failed judge is not silently absent from the run's record. It carries no route, usage
/// or timing — only which question failed and why. Snake_case keys are a cross-repo contract with
/// the server's parsing mirror — do not rename without updating both.</summary>
public sealed record BaselineQuestionFailure {
    [JsonPropertyName("question_id")] public string QuestionId { get; init; } = "";
    [JsonPropertyName("category")]    public string Category   { get; init; } = "";
    [JsonPropertyName("reason")]      public string Reason     { get; init; } = "";
}
