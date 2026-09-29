using System.Text.Json.Serialization;
using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Core.Eval;

/// <summary>Token usage and self-reported cost for one judge invocation, or a run's total, as
/// written to <c>--baseline-out</c>. Snake_case keys are a cross-repo contract with the server's
/// parsing mirror — do not rename without updating both.</summary>
public sealed record EvalUsage {
    [JsonPropertyName("input_tokens")]       public long    InputTokens      { get; init; }
    [JsonPropertyName("output_tokens")]      public long    OutputTokens     { get; init; }
    [JsonPropertyName("cache_read_tokens")]  public long    CacheReadTokens  { get; init; }
    [JsonPropertyName("cache_write_tokens")] public long    CacheWriteTokens { get; init; }
    [JsonPropertyName("reported_cost_usd")]  public double? ReportedCostUsd  { get; init; }

    internal static EvalUsage FromResult(ClaudeCliResult result) => new() {
        InputTokens      = result.InputTokens,
        OutputTokens     = result.OutputTokens,
        CacheReadTokens  = result.CacheReadTokens,
        CacheWriteTokens = result.CacheWriteTokens,
        ReportedCostUsd  = result.CostUsd
    };

    /// <summary>Sums token counts across every entry; cost sums only the entries that reported one,
    /// staying null when none did.</summary>
    public static EvalUsage Sum(IEnumerable<EvalUsage> usages) {
        long    inputTokens = 0, outputTokens = 0, cacheReadTokens = 0, cacheWriteTokens = 0;
        double? cost        = null;

        foreach (var u in usages) {
            inputTokens      += u.InputTokens;
            outputTokens     += u.OutputTokens;
            cacheReadTokens  += u.CacheReadTokens;
            cacheWriteTokens += u.CacheWriteTokens;
            if (u.ReportedCostUsd is { } c) cost = (cost ?? 0) + c;
        }

        return new EvalUsage {
            InputTokens      = inputTokens,
            OutputTokens     = outputTokens,
            CacheReadTokens  = cacheReadTokens,
            CacheWriteTokens = cacheWriteTokens,
            ReportedCostUsd  = cost
        };
    }
}
