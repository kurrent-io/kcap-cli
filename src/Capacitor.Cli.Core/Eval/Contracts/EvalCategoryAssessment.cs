using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Eval.Contracts;

/// <summary>Mirrors the server's <c>JudgeCategoryAssessment</c> field for field. <c>Score</c> is
/// null when the category has no assessed question.</summary>
public record EvalCategoryAssessment {
    [JsonPropertyName("name")]      public required string Name { get; init; }
    [JsonPropertyName("score")]     public int?    Score   { get; init; }
    [JsonPropertyName("verdict")]   public string? Verdict { get; init; }
    [JsonPropertyName("questions")] public List<EvalQuestionAssessment> Questions { get; init; } = [];
}
