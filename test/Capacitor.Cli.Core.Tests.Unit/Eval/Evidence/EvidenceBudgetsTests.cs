using Capacitor.Cli.Core.Eval.Evidence;

namespace Capacitor.Cli.Core.Tests.Unit.Eval.Evidence;

/// <summary>At the CLI's first-view budget the first section of the worst-case completion (34 %), safety (50 %) and
/// efficiency (100 %) views is delivered; at one page budget none is — the arithmetic behind choosing 196 608.</summary>
public class EvidenceBudgetsTests {
    [Test]
    [Arguments(34, 65_536)]
    [Arguments(50, 94_208)]
    [Arguments(100, 188_416)]
    public async Task The_first_section_fits_at_the_first_view_budget_and_not_at_one_page(int sharePercent, int expected) {
        await Assert.That(EvidenceBudgets.FirstSectionBudget(EvidenceBudgets.FirstViewBytes, sharePercent)).IsEqualTo(expected);
        await Assert.That(EvidenceBudgets.FirstSectionBudget(65_536, sharePercent)).IsNull();
    }
}
