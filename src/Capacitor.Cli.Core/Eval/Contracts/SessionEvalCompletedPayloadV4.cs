using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Eval.Contracts;

/// <summary>Posted to <c>POST /api/sessions/{id}/evals/v4</c>. Mirrors the server's
/// <c>JudgeRunReportV4</c> field for field (the server fills <c>evaluated_at</c>).</summary>
public record SessionEvalCompletedPayloadV4 {
    [JsonPropertyName("eval_run_id")]   public required string EvalRunId  { get; init; }
    [JsonPropertyName("judge_model")]   public required string JudgeModel { get; init; }
    [JsonPropertyName("categories")]    public List<EvalCategoryAssessment> Categories { get; init; } = [];
    [JsonPropertyName("overall_score")] public int?    OverallScore { get; init; }
    [JsonPropertyName("summary")]       public required string Summary { get; init; }

    [JsonPropertyName("retrospective")]                public EvalRetrospectiveV2? Retrospective { get; init; }
    [JsonPropertyName("retrospective_prompt_version")] public string? RetrospectivePromptVersion { get; init; }
    [JsonPropertyName("facts_used")]                   public List<EvalFactSnapshotPayload> FactsUsed { get; init; } = [];

    [JsonPropertyName("assessed_questions")]   public required int AssessedQuestions   { get; init; }
    [JsonPropertyName("unassessed_questions")] public required int UnassessedQuestions { get; init; }
    [JsonPropertyName("judged_questions")]     public required int JudgedQuestions     { get; init; }
    [JsonPropertyName("total_questions")]      public required int TotalQuestions      { get; init; }
    [JsonPropertyName("failed_questions")]     public List<EvalQuestionFailure> FailedQuestions { get; init; } = [];

    [JsonPropertyName("coverage_policy_version")] public required string CoveragePolicyVersion { get; init; }
    [JsonPropertyName("evidence_scope_version")]  public string? EvidenceScopeVersion { get; init; }
}
