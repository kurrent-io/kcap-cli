using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Eval.Contracts;

/// <summary>The V4 per-question judge result. Mirrors the server's <c>JudgeQuestionAssessment</c>
/// field for field, minus <c>trace_coverage</c> — a turn-navigating-only field the CLI never
/// produces, so omitting it is wire-identical to always sending it null.
///
/// <para><c>Outcome</c> is nullable rather than defaulted: System.Text.Json constructs a type with
/// `required` members through an uninitialized object, which skips property initializers, so a
/// judge response that omits the field would silently bind an empty string instead of the intended
/// default. <see cref="Capacitor.Cli.Core.Eval.EvalService.ParseVerdict"/> coalesces a null/missing
/// outcome to <see cref="EvalOutcomes.Assessed"/> after deserializing.</para>
/// </summary>
public record EvalQuestionAssessment {
    [JsonPropertyName("category")]       public required string Category   { get; init; }
    [JsonPropertyName("question_id")]    public required string QuestionId { get; init; }
    [JsonPropertyName("outcome")]        public string? Outcome { get; init; }
    [JsonPropertyName("score")]          public int?    Score          { get; init; }
    [JsonPropertyName("verdict")]        public string? Verdict        { get; init; }
    [JsonPropertyName("finding")]        public required string Finding { get; init; }
    [JsonPropertyName("evidence")]       public string? Evidence       { get; init; }
    [JsonPropertyName("recommendation")] public string? Recommendation { get; init; }
    [JsonPropertyName("tools_used")]     public int?    ToolsUsed      { get; init; }
    [JsonPropertyName("prompt_version")] public string? PromptVersion  { get; init; }

    [JsonPropertyName("evidence_coverage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EvalEvidenceCoverage? EvidenceCoverage { get; init; }
}
