using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Eval;

/// <summary>One question's record in a <c>--baseline-out</c> file: its route (<c>legacy_text</c>,
/// <c>legacy_tools</c>, <c>evidence_one_shot</c>, <c>evidence_retrieval</c>), judge usage, runner calls,
/// wall time and, on the evidence route, the retained ledger copy. Snake_case keys are a
/// cross-repo contract with the server's parsing mirror — do not rename without updating both.</summary>
public sealed record BaselineQuestionOutput {
    [JsonPropertyName("question_id")] public string    QuestionId { get; init; } = "";
    [JsonPropertyName("route")]       public string    Route      { get; init; } = "";
    [JsonPropertyName("usage")]       public EvalUsage Usage      { get; init; } = new();
    [JsonPropertyName("calls")]       public int       Calls      { get; init; }
    [JsonPropertyName("elapsed_ms")]  public long      ElapsedMs  { get; init; }

    [JsonPropertyName("ledger_path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LedgerPath { get; init; }
}
