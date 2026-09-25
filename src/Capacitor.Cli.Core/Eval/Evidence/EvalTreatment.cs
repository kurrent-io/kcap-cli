using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Eval.Evidence;

/// <summary>What a run was configured with: whether the server advertised the evidence route (whatever route
/// the run then took), its budgets and the prompt-resource hashes. Not advertised is exactly the empty gate-off record.</summary>
public sealed record EvalTreatment {
    [JsonPropertyName("gate_on")]         public required bool                     GateOn         { get; init; }
    [JsonPropertyName("budgets")]         public          Dictionary<string, long>   Budgets        { get; init; } = [];
    [JsonPropertyName("preamble_hashes")] public          Dictionary<string, string> PreambleHashes { get; init; } = [];

    public static EvalTreatment None => new() { GateOn = false };

    public static EvalTreatment For(EvalCatalogDto catalog) => catalog.EvidenceRetrieval is not { } ad ? None : new() {
        GateOn = true,
        Budgets = new Dictionary<string, long>(StringComparer.Ordinal) {
            ["MaxToolCalls"]               = ad.MaxToolCalls,
            ["JudgeByteBudgetBytes"]       = ad.JudgeByteBudgetBytes,
            ["PageBudgetBytes"]            = ad.PageBudgetBytes,
            ["OneShotLimitChars"]          = ad.OneShotLimitChars,
            ["RetrospectiveEvidenceBytes"] = ad.RetrospectiveEvidenceBytes,
            ["FirstViewBytes"]             = EvidenceBudgets.FirstViewBytes,
            ["MaxSpendUsdCents"]           = EvidenceBudgets.MaxSpendUsdCents,
            ["TraceTokenBudget"]           = EvalService.TraceTokenBudget()
        },
        PreambleHashes = EvidencePreambleHashes.Compute()
    };
}
