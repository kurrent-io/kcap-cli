using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Eval;

/// <summary>The retrospective synthesis record in a <c>--baseline-out</c> file — its judge usage,
/// runner invocation count, and wall time. Snake_case keys are a cross-repo contract with the
/// server's parsing mirror — do not rename without updating both.</summary>
public sealed record BaselineRetrospectiveOutput {
    [JsonPropertyName("usage")]      public EvalUsage Usage     { get; init; } = new();
    [JsonPropertyName("calls")]      public int       Calls     { get; init; }
    [JsonPropertyName("elapsed_ms")] public long      ElapsedMs { get; init; }
}
