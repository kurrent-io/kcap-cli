using System.Text;
using System.Text.Json.Serialization;
using Capacitor.Cli.Core.Eval.Evidence;

namespace Capacitor.Cli.Core.Eval.Contracts;

/// <summary>The V4 per-question judge result. Mirrors the server's <c>JudgeQuestionAssessment</c>
/// field for field; every optional field is omitted when null.
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

    [JsonPropertyName("trace_coverage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EvalTraceCoverage? TraceCoverage { get; init; }

    [JsonPropertyName("strategy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Strategy { get; init; }

    [JsonPropertyName("strategy_version")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? StrategyVersion { get; init; }

    [JsonPropertyName("obligations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<EvalObligationResult>? Obligations { get; init; }

    [JsonPropertyName("obligations_not_reported")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ObligationsNotReported { get; init; }

    static readonly IReadOnlySet<string> ObligationLossReasons = new HashSet<string>(StringComparer.Ordinal) { "truncated", "unparseable", "refused", "budget_stop", "uncertified" };

    /// <summary>The server's per-question rules the CLI can break; null when the V4 route would accept the record.</summary>
    public string? Validate() {
        if (string.IsNullOrWhiteSpace(Category))   return "category must be non-empty";
        if (string.IsNullOrWhiteSpace(QuestionId)) return "question_id must be non-empty";
        var outcome = Outcome ?? EvalOutcomes.Assessed;
        if (!EvalOutcomes.All.Contains(outcome))   return $"outcome '{outcome}' is unknown for '{QuestionId}'";
        if (string.IsNullOrWhiteSpace(Finding))    return $"finding must be non-empty for '{QuestionId}'";

        if (outcome == EvalOutcomes.Assessed) {
            if (Score is null or < 1 or > 5)                      return $"score must be 1..5 when assessed for '{QuestionId}'";
            if (Verdict != EvalService.VerdictForScore(Score.Value)) return $"verdict '{Verdict}' does not match score {Score} for '{QuestionId}'";
        } else {
            if (Score is not null)   return $"score must be null when outcome is {outcome} for '{QuestionId}'";
            if (Verdict is not null) return $"verdict must be null when outcome is {outcome} for '{QuestionId}'";
        }

        if (TraceCoverage?.Validate() is { } traceError)       return $"trace_coverage invalid for '{QuestionId}': {traceError}";
        if (EvidenceCoverage?.Validate() is { } coverageError) return $"evidence_coverage invalid for '{QuestionId}': {coverageError}";
        if (ObligationsNotReported is not null && !ObligationLossReasons.Contains(ObligationsNotReported))
            return $"obligations_not_reported '{ObligationsNotReported}' is unknown for '{QuestionId}'";
        if (ValidateStrategy() is { } strategyError) return strategyError;
        if (ValidateObligations(outcome) is { } obligationError) return $"{obligationError} for '{QuestionId}'";

        if (EvidenceCoverage is not { } ec) return null;
        if (outcome == EvalOutcomes.InsufficientEvidence && ec.IsComplete)
            return $"evidence_coverage cannot be complete when outcome is insufficient_evidence for '{QuestionId}'";
        if (TraceCoverage is not { } tc) return null;
        if (tc.Mode == EvalTraceCoverage.EvidenceRetrievalMode) {
            if (ec.PolicyVersion == EvalService.CoveragePolicyVersion)
                return $"trace_coverage.mode evidence_retrieval cannot sit beside a coverage-v1 record for '{QuestionId}'";
            if (tc.BudgetTripped == true && ec.StopReason is null or EvalStopReasons.JudgeStopped)
                return $"trace_coverage.budget_tripped requires a budget stop_reason for '{QuestionId}'";
        }
        if (tc.TraceTrimmed == true && ec.Omissions.All(o => o.Kind != EvalOmissionKinds.TraceTailTrimmed))
            return $"trace_coverage.trace_trimmed requires a trace_tail_trimmed omission for '{QuestionId}'";
        return null;
    }

    string? ValidateStrategy() {
        if ((Strategy is null) != (StrategyVersion is null)) return $"strategy and strategy_version must be set together for '{QuestionId}'";
        if (Strategy is not null && !IsStrategyForm(Strategy))               return $"strategy must be 1-64 characters of a-z 0-9 _ - for '{QuestionId}'";
        if (StrategyVersion is not null && !IsStrategyForm(StrategyVersion)) return $"strategy_version must be 1-64 characters of a-z 0-9 _ - for '{QuestionId}'";
        return null;
    }

    string? ValidateObligations(string outcome) {
        if (Obligations is null) return null;
        if (Obligations.Count == 0)                                     return "obligations must be omitted rather than empty";
        if (Obligations.Count > EvalObligationContract.MaxObligations) return $"obligations exceeds {EvalObligationContract.MaxObligations}";
        if (outcome == EvalOutcomes.NotApplicable)                      return "obligations cannot accompany outcome not_applicable";

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Obligations.Count; i++) {
            var o = Obligations[i];
            if (o is null)                                                                 return $"obligations[{i}] must be an object";
            if (string.IsNullOrWhiteSpace(o.Title))                                        return $"obligations[{i}].title must be non-empty";
            if (Encoding.UTF8.GetByteCount(o.Title) > EvalObligationContract.MaxTextBytes) return $"obligations[{i}].title exceeds {EvalObligationContract.MaxTextBytes} bytes";
            if (o.Note is not null && Encoding.UTF8.GetByteCount(o.Note) > EvalObligationContract.MaxTextBytes) return $"obligations[{i}].note exceeds {EvalObligationContract.MaxTextBytes} bytes";
            if (!EvalObligationContract.Origins.Contains(o.Origin))                        return $"obligations[{i}].origin '{o.Origin}' is unknown";
            if (!EvalObligationContract.Statuses.Contains(o.Status))                       return $"obligations[{i}].status '{o.Status}' is unknown";
            if (o.Anchor is null || !EvidenceRefText.TryParse(o.Anchor.Ref, out _))        return $"obligations[{i}].anchor.ref is not an evidence ref";
            if (!IsDigest(o.Anchor.Digest))                                                return $"obligations[{i}].anchor.digest must be 64 lower-case hex characters";
            if (o.Citations is null)                                                       return $"obligations[{i}].citations must be an array";
            if (o.Citations.Count > EvalObligationContract.MaxCitations)                   return $"obligations[{i}].citations exceeds {EvalObligationContract.MaxCitations}";

            foreach (var c in o.Citations) {
                if (c is null || !EvidenceRefText.TryParse(c.Ref, out _)) return $"obligations[{i}].citations holds a value that is not an evidence ref";
                if (!IsDigest(c.Digest))                                  return $"obligations[{i}].citations digest must be 64 lower-case hex characters";
                if (c.Ref == o.Anchor.Ref)                                return $"obligations[{i}].citations repeats the anchor";
            }

            if (EvalObligationRules.IsDecisive(o.Status) && o.Citations.Count == 0) return $"obligations[{i}] is {o.Status} and needs at least one citation besides its anchor";
            if (o.Id != EvalObligationRules.DeriveId(o.Anchor.Ref, o.Title))        return $"obligations[{i}].id does not match its anchor and title";
            if (!ids.Add(o.Id))                                                     return $"obligations[{i}].id is a duplicate";
        }

        if (outcome == EvalOutcomes.Assessed && !Obligations.Any(o => EvalObligationRules.IsDecisive(o.Status)))
            return "an assessed outcome with obligations needs at least one decisive status";
        return null;
    }

    static bool IsStrategyForm(string s) => s.Length is >= 1 and <= 64 && s.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-');

    static bool IsDigest(string? s) => s is { Length: 64 } && s.All(ch => ch is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
