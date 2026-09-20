using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Eval;

/// <summary>One question's record in a <c>--baseline-out</c> file: how <c>kcap eval</c> routed it,
/// its judge usage, how many times the runner was called, and wall time. Snake_case keys are a
/// cross-repo contract with the server's parsing mirror — do not rename without updating both.</summary>
public sealed record BaselineQuestionOutput {
    [JsonPropertyName("question_id")] public string    QuestionId { get; init; } = "";
    [JsonPropertyName("route")]       public string    Route      { get; init; } = "";
    [JsonPropertyName("usage")]       public EvalUsage Usage      { get; init; } = new();
    [JsonPropertyName("calls")]       public int       Calls      { get; init; }
    [JsonPropertyName("elapsed_ms")]  public long      ElapsedMs  { get; init; }
}
