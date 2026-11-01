using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Setup.Intelligence;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>A provider's key comes from its own variable, else a hidden prompt where one can be asked, else a refusal
/// naming the variable</summary>
public class IntelligenceKeyTests {
    [Test]
    public async Task The_variable_wins_without_asking() {
        var (key, error) = IntelligenceKey.Resolve(IntelligenceVendor.Anthropic,
            name => name == "ANTHROPIC_API_KEY" ? " sk-env " : null, () => throw new InvalidOperationException("asked"));

        await Assert.That(key).IsEqualTo("sk-env");
        await Assert.That(error).IsNull();
    }

    [Test]
    public async Task Without_it_the_prompt_answers() {
        var (key, _) = IntelligenceKey.Resolve(IntelligenceVendor.Cohere, _ => null, () => "co-typed");

        await Assert.That(key).IsEqualTo("co-typed");
    }

    [Test]
    public async Task With_neither_the_refusal_names_the_variable() {
        var (key, error) = IntelligenceKey.Resolve(IntelligenceVendor.OpenRouter, _ => "  ", null);

        await Assert.That(key).IsNull();
        await Assert.That(error).Contains("OPENROUTER_API_KEY");
    }

    [Test]
    public async Task An_empty_prompt_answer_is_a_refusal() {
        var (key, error) = IntelligenceKey.Resolve(IntelligenceVendor.OpenAI, _ => null, () => "");

        await Assert.That(key).IsNull();
        await Assert.That(error).IsNotNull();
    }
}
