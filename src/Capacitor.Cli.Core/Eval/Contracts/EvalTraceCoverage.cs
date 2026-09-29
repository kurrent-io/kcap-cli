using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Eval.Contracts;

/// <summary>Mirrors the server's <c>JudgeTraceCoverage</c> for the two modes a CLI run produces.</summary>
public record EvalTraceCoverage {
    public const string OneShotMode           = "one_shot";
    public const string EvidenceRetrievalMode = "evidence_retrieval";

    [JsonPropertyName("mode")] public required string Mode { get; init; }
    [JsonPropertyName("budget_tripped")]    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? BudgetTripped   { get; init; }
    [JsonPropertyName("delivered_bytes")]   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public long? DeliveredBytes  { get; init; }
    [JsonPropertyName("budget_bytes")]      [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public long? BudgetBytes     { get; init; }
    [JsonPropertyName("turns_fetched")]     [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int?  TurnsFetched    { get; init; }
    [JsonPropertyName("turns_total")]       [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int?  TurnsTotal      { get; init; }
    [JsonPropertyName("iterations_used")]   [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int?  IterationsUsed  { get; init; }
    [JsonPropertyName("max_iterations")]    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int?  MaxIterations   { get; init; }
    [JsonPropertyName("tool_calls")]        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int?  ToolCalls       { get; init; }
    [JsonPropertyName("max_tool_calls")]    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int?  MaxToolCalls    { get; init; }
    [JsonPropertyName("trace_trimmed")]     [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? TraceTrimmed    { get; init; }
    [JsonPropertyName("trace_chars")]       [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int?  TraceChars      { get; init; }
    [JsonPropertyName("trace_total_chars")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public long? TraceTotalChars { get; init; }
    [JsonPropertyName("trace_limit_chars")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int?  TraceLimitChars { get; init; }

    public static EvalTraceCoverage ForOneShot(bool traceTrimmed, int traceChars, long traceTotalChars, int traceLimitChars) => new() {
        Mode = OneShotMode, TraceTrimmed = traceTrimmed, TraceChars = traceChars, TraceTotalChars = traceTotalChars, TraceLimitChars = traceLimitChars
    };

    public static EvalTraceCoverage ForEvidenceRetrieval(bool budgetTripped, long deliveredBytes, long budgetBytes, int iterationsUsed, int maxIterations, int toolCalls, int maxToolCalls) => new() {
        Mode = EvidenceRetrievalMode, BudgetTripped = budgetTripped, DeliveredBytes = deliveredBytes, BudgetBytes = budgetBytes,
        IterationsUsed = iterationsUsed, MaxIterations = maxIterations, ToolCalls = toolCalls, MaxToolCalls = maxToolCalls
    };

    public string? Validate() => Mode switch {
        OneShotMode           => ValidateOneShot(),
        EvidenceRetrievalMode => ValidateEvidenceRetrieval(),
        _                     => $"unknown mode '{Mode}'"
    };

    string? ValidateEvidenceRetrieval() {
        if (BudgetTripped is null || DeliveredBytes is null || BudgetBytes is null || IterationsUsed is null || MaxIterations is null || ToolCalls is null || MaxToolCalls is null)
            return "evidence_retrieval requires budget_tripped, delivered_bytes, budget_bytes, iterations_used, max_iterations, tool_calls and max_tool_calls";
        if (TurnsFetched is not null || TurnsTotal is not null || TraceTrimmed is not null || TraceChars is not null || TraceTotalChars is not null || TraceLimitChars is not null)
            return "evidence_retrieval must not carry one_shot or turns fields";
        if (DeliveredBytes < 0 || DeliveredBytes > BudgetBytes)   return "delivered_bytes out of range";
        if (IterationsUsed < 0 || IterationsUsed > MaxIterations) return "iterations_used out of range";
        if (ToolCalls < 0 || ToolCalls > MaxToolCalls)            return "tool_calls out of range";
        return null;
    }

    string? ValidateOneShot() {
        if (TraceTrimmed is null || TraceChars is null || TraceTotalChars is null || TraceLimitChars is null) return "one_shot requires all one_shot fields";
        if (BudgetTripped is not null || DeliveredBytes is not null || BudgetBytes is not null || TurnsFetched is not null || TurnsTotal is not null || IterationsUsed is not null || MaxIterations is not null)
            return "one_shot must not carry turn_navigating fields";
        if (ToolCalls is not null || MaxToolCalls is not null) return "tool_calls and max_tool_calls belong to evidence_retrieval";
        return null;
    }
}
