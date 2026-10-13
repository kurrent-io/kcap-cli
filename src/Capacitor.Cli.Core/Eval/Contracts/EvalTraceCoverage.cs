using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Eval.Contracts;

/// <summary>Mirrors the server's <c>JudgeTraceCoverage</c>: the two modes a CLI run produces, and the extract mode a server run
/// records.</summary>
public record EvalTraceCoverage {
    public const string OneShotMode           = "one_shot";
    public const string EvidenceRetrievalMode = "evidence_retrieval";
    public const string EvidenceExtractMode   = "evidence_extract";

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
    [JsonPropertyName("chunk_count")]          [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int?  ChunkCount          { get; init; }
    [JsonPropertyName("chunks_extracted")]     [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int?  ChunksExtracted     { get; init; }
    [JsonPropertyName("records_total")]        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int?  RecordsTotal        { get; init; }
    [JsonPropertyName("records_delivered")]    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int?  RecordsDelivered    { get; init; }
    [JsonPropertyName("slice_count")]          [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int?  SliceCount          { get; init; }
    [JsonPropertyName("slices_judged")]        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int?  SlicesJudged        { get; init; }
    [JsonPropertyName("prescreen_candidates")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int?  PrescreenCandidates { get; init; }
    [JsonPropertyName("evidence_incomplete")]  [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? EvidenceIncomplete  { get; init; }

    const string ExtractFieldsMisplaced = "the evidence_extract fields belong to evidence_extract";

    bool HasExtractFields => ChunkCount is not null || ChunksExtracted is not null || RecordsTotal is not null || RecordsDelivered is not null
                          || SliceCount is not null || SlicesJudged is not null || PrescreenCandidates is not null || EvidenceIncomplete is not null;

    /// <summary>Whether the verdict was scored from partial detail.</summary>
    [JsonIgnore] public bool IsPartial =>
        BudgetTripped == true || TraceTrimmed == true
     || Mode == EvidenceExtractMode
        && (ChunksExtracted < ChunkCount || RecordsDelivered < RecordsTotal || SlicesJudged < SliceCount || EvidenceIncomplete == true);

    public static EvalTraceCoverage ForOneShot(bool traceTrimmed, int traceChars, long traceTotalChars, int traceLimitChars) => new() {
        Mode = OneShotMode, TraceTrimmed = traceTrimmed, TraceChars = traceChars, TraceTotalChars = traceTotalChars, TraceLimitChars = traceLimitChars
    };

    public static EvalTraceCoverage ForEvidenceRetrieval(bool budgetTripped, long deliveredBytes, long budgetBytes, int iterationsUsed, int maxIterations, int toolCalls, int maxToolCalls) => new() {
        Mode = EvidenceRetrievalMode, BudgetTripped = budgetTripped, DeliveredBytes = deliveredBytes, BudgetBytes = budgetBytes,
        IterationsUsed = iterationsUsed, MaxIterations = maxIterations, ToolCalls = toolCalls, MaxToolCalls = maxToolCalls
    };

    public static EvalTraceCoverage ForEvidenceExtract(int chunkCount, int chunksExtracted, int recordsTotal, int recordsDelivered,
            int? sliceCount = null, int? slicesJudged = null, int? prescreenCandidates = null, bool? evidenceIncomplete = null) => new() {
        Mode = EvidenceExtractMode, ChunkCount = chunkCount, ChunksExtracted = chunksExtracted, RecordsTotal = recordsTotal, RecordsDelivered = recordsDelivered,
        SliceCount = sliceCount, SlicesJudged = slicesJudged, PrescreenCandidates = prescreenCandidates, EvidenceIncomplete = evidenceIncomplete
    };

    public string? Validate() => Mode switch {
        OneShotMode           => ValidateOneShot(),
        EvidenceRetrievalMode => ValidateEvidenceRetrieval(),
        EvidenceExtractMode   => ValidateEvidenceExtract(),
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
        if (HasExtractFields) return ExtractFieldsMisplaced;
        return null;
    }

    string? ValidateOneShot() {
        if (TraceTrimmed is null || TraceChars is null || TraceTotalChars is null || TraceLimitChars is null) return "one_shot requires all one_shot fields";
        if (BudgetTripped is not null || DeliveredBytes is not null || BudgetBytes is not null || TurnsFetched is not null || TurnsTotal is not null || IterationsUsed is not null || MaxIterations is not null)
            return "one_shot must not carry turn_navigating fields";
        if (ToolCalls is not null || MaxToolCalls is not null) return "tool_calls and max_tool_calls belong to evidence_retrieval";
        if (HasExtractFields) return ExtractFieldsMisplaced;
        if (TraceChars < 0)       return "trace_chars must be non-negative";
        if (TraceTotalChars < 0)  return "trace_total_chars must be non-negative";
        if (TraceLimitChars <= 0) return "trace_limit_chars must be positive";
        return null;
    }

    string? ValidateEvidenceExtract() {
        if (ChunkCount is null || ChunksExtracted is null || RecordsTotal is null || RecordsDelivered is null)
            return "evidence_extract requires chunk_count, chunks_extracted, records_total and records_delivered";
        if (BudgetTripped is not null || DeliveredBytes is not null || BudgetBytes is not null || TurnsFetched is not null || TurnsTotal is not null
         || IterationsUsed is not null || MaxIterations is not null || ToolCalls is not null || MaxToolCalls is not null
         || TraceTrimmed is not null || TraceChars is not null || TraceTotalChars is not null || TraceLimitChars is not null)
            return "evidence_extract must not carry the fields of another mode";
        if (ChunkCount < 0 || ChunksExtracted < 0 || ChunksExtracted > ChunkCount)      return "chunks_extracted out of range";
        if (RecordsTotal < 0 || RecordsDelivered < 0 || RecordsDelivered > RecordsTotal) return "records_delivered out of range";
        if (SliceCount < 2)                                 return "slice_count must be null or at least 2";
        if ((SliceCount is null) != (SlicesJudged is null)) return "slice_count and slices_judged must be set together";
        if (SlicesJudged < 0 || SlicesJudged > SliceCount)  return "slices_judged out of range";
        if (PrescreenCandidates < 0)                        return "prescreen_candidates must be non-negative";
        return null;
    }
}
