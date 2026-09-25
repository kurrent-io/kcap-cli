using System.Text.Json;
using Capacitor.Cli.Core.Eval.Evidence;

namespace Capacitor.Cli.Core.Tests.Unit.Eval.Evidence;

/// <summary>Without an advertisement the treatment is exactly the empty gate-off record S8's baseline side compares equal to;
/// with one it carries all eight budget keys, the child's trace token budget and the three resource hashes.</summary>
public class EvalTreatmentTests {
    static EvalCatalogDto Catalog(EvalEvidenceAdvertisementDto? ad) => new() { RetrospectivePrompt = "r", RetrospectivePromptVersion = "1", EvidenceRetrieval = ad };

    static readonly EvalEvidenceAdvertisementDto Ad = new() {
        MaxToolCalls = 48, JudgeByteBudgetBytes = 600_000, PageBudgetBytes = 65_536, OneShotLimitChars = 400_000, RetrospectiveEvidenceBytes = 200_000, CoveragePolicyVersion = "coverage-v2"
    };

    [Test]
    public async Task Not_advertised_is_exactly_the_empty_gate_off_record() {
        var treatment = EvalTreatment.For(Catalog(null));

        await Assert.That(JsonSerializer.Serialize(treatment, CapacitorJsonContext.Default.EvalTreatment)).IsEqualTo("""{"gate_on":false,"budgets":{},"preamble_hashes":{}}""");
        await Assert.That(JsonSerializer.Serialize(EvalTreatment.None, CapacitorJsonContext.Default.EvalTreatment)).IsEqualTo("""{"gate_on":false,"budgets":{},"preamble_hashes":{}}""");
    }

    [Test]
    [NotInParallel]
    public async Task Advertised_carries_all_eight_budgets_and_the_resource_hashes() {
        using var budget = EnvScope.Exclusive("KCAP_EVAL_TRACE_TOKEN_BUDGET", "150000");

        var treatment = EvalTreatment.For(Catalog(Ad));

        await Assert.That(treatment.GateOn).IsTrue();
        await Assert.That(treatment.Budgets).IsEquivalentTo(new Dictionary<string, long> {
            ["MaxToolCalls"] = 48, ["JudgeByteBudgetBytes"] = 600_000, ["PageBudgetBytes"] = 65_536, ["OneShotLimitChars"] = 400_000,
            ["RetrospectiveEvidenceBytes"] = 200_000, ["FirstViewBytes"] = 196_608, ["MaxSpendUsdCents"] = 100, ["TraceTokenBudget"] = 150_000
        });
        await Assert.That(treatment.PreambleHashes).IsEquivalentTo(EvidencePreambleHashes.Compute());
    }
}
