using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>The evidence-route budgets the server advertises on its eval catalog; present only when the tenant enabled the route.</summary>
public sealed record EvalEvidenceAdvertisementDto {
    [JsonPropertyName("max_tool_calls")]               public required int    MaxToolCalls               { get; init; }
    [JsonPropertyName("judge_byte_budget_bytes")]      public required long   JudgeByteBudgetBytes       { get; init; }
    [JsonPropertyName("page_budget_bytes")]            public required int    PageBudgetBytes            { get; init; }
    [JsonPropertyName("one_shot_limit_chars")]         public required int    OneShotLimitChars          { get; init; }
    [JsonPropertyName("retrospective_evidence_bytes")] public required int    RetrospectiveEvidenceBytes { get; init; }
    [JsonPropertyName("coverage_policy_version")]      public required string CoveragePolicyVersion      { get; init; }
}
