using Capacitor.Cli.Core.Setup.Intelligence;

namespace Capacitor.Cli.Core.Tests.Unit.Setup.Intelligence;

/// <summary>Each server code reads as a sentence, carrying the detail after its colon; an unknown one passes through</summary>
public class IntelligenceRefusalsTests {
    [Test]
    [Arguments("key_rejected: 401 invalid x-api-key", "401 invalid x-api-key")]
    [Arguments("key_pinned_above_store: anthropic", "anthropic")]
    [Arguments("not_offered", "local agent")]
    [Arguments("model_required", "model")]
    [Arguments("something_new", "something_new")]
    public async Task A_code_reads_as_a_sentence(string code, string contains) =>
        await Assert.That(IntelligenceRefusals.Describe(code)).Contains(contains);
}
