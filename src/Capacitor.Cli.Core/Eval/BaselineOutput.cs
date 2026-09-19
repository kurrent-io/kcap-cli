using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Eval;

/// <summary>The full record <c>kcap eval --baseline-out</c> writes for one run. Snake_case keys are
/// a cross-repo contract with the server's parsing mirror — do not rename without updating both.</summary>
public sealed record BaselineOutput {
    [JsonPropertyName("session_id")]       public string                                SessionId      { get; init; } = "";
    [JsonPropertyName("eval_run_id")]      public string                                EvalRunId      { get; init; } = "";
    [JsonPropertyName("model")]            public string                                Model          { get; init; } = "";
    [JsonPropertyName("chain")]            public bool                                  Chain          { get; init; }
    [JsonPropertyName("questions")]        public IReadOnlyList<BaselineQuestionOutput> Questions      { get; init; } = [];
    [JsonPropertyName("retrospective")]    public BaselineRetrospectiveOutput?          Retrospective  { get; init; }
    [JsonPropertyName("totals")]           public EvalUsage                             Totals         { get; init; } = new();
    [JsonPropertyName("total_elapsed_ms")] public long                                  TotalElapsedMs { get; init; }
}
