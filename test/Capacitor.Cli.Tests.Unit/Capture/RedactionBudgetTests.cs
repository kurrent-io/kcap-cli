using Capacitor.Cli.Capture;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Capture;

public class RedactionBudgetTests {
    [Test]
    public async Task ChecksShareOneElapsedBudget() {
        var time = new FakeTimeProvider();
        var budget = new RedactionBudget(time);
        time.Advance(TimeSpan.FromMilliseconds(600));
        budget.Check();
        time.Advance(TimeSpan.FromMilliseconds(400));
        await Assert.That(budget.Check).Throws<RedactionBudgetExceededException>();
    }

    [Test]
    public async Task UnlimitedBudgetNeverExpires() {
        var time = new FakeTimeProvider();
        var budget = new RedactionBudget(time, TimeSpan.MaxValue);
        time.Advance(TimeSpan.FromDays(365));
        await Assert.That(budget.Check).ThrowsNothing();
    }

    [Test]
    public async Task RecordDoesNotRenewBudgetForEachValue() {
        var time = new AdvancingRedactionTimeProvider();
        var line = "[" + string.Join(",", Enumerable.Repeat("\"plain\"", 200)) + "]";
        var outcome = SecretRedactor.RedactLineWithOutcome(line, time);
        await Assert.That(outcome.Loss).IsEqualTo(RedactionLossReason.RecordBudget);
        await Assert.That(outcome.Line).DoesNotContain("plain");
    }
}
