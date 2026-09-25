namespace Capacitor.Cli.Core.Eval.Evidence;

public static class EvidenceRouteChoice {
    /// <summary>The one-shot bound F: the advertised limit, capped by the trace token budget at four characters per token.</summary>
    public static int OneShotLimitChars(int advertisedLimitChars, int traceTokenBudget) =>
        (int)Math.Min(advertisedLimitChars, (long)traceTokenBudget * 4);

    /// <summary>Retrieval unless the whole trace fits F. A failed read is left on the trace for the caller to classify.</summary>
    public static async Task<(EvidenceRoute Route, EvidenceTraceResult Trace)> ChooseAsync(
            EvidenceTraceAssembler assembler, IReadOnlyList<EvidenceSourceDto> sources, int limitChars, CancellationToken ct) {
        var trace = await assembler.AssembleAsync(sources, limitChars, ct);
        return (trace.Fits ? EvidenceRoute.OneShot : EvidenceRoute.Retrieval, trace);
    }
}
