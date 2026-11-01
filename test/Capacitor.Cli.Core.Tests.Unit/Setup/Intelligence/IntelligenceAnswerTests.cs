using Capacitor.Cli.Core.Setup.Intelligence;

namespace Capacitor.Cli.Core.Tests.Unit.Setup.Intelligence;

/// <summary>One token names the whole answer, and nothing outside the offered set parses</summary>
public class IntelligenceAnswerTests {
    [Test]
    [Arguments("claude", "local_agent", "claude", null)]
    [Arguments("Codex", "local_agent", "codex", null)]
    [Arguments("anthropic", "provider", null, "anthropic")]
    [Arguments("openai", "provider", null, "openai")]
    [Arguments("openrouter", "provider", null, "openrouter")]
    [Arguments(" cohere ", "provider", null, "cohere")]
    [Arguments("none", "none", null, null)]
    public async Task A_token_parses_to_its_answer(string token, string mode, string? harness, string? vendor) {
        var answer = IntelligenceAnswer.Parse(token);

        await Assert.That(answer?.Mode).IsEqualTo(mode);
        await Assert.That(answer?.Harness).IsEqualTo(harness);
        await Assert.That(answer?.Vendor?.Id).IsEqualTo(vendor);
    }

    [Test]
    [Arguments("gemini")]
    [Arguments("")]
    [Arguments(null)]
    [Arguments("local_agent")]
    public async Task Anything_else_does_not_parse(string? token) =>
        await Assert.That(IntelligenceAnswer.Parse(token)).IsNull();

    [Test]
    public async Task A_provider_sends_no_model_where_the_server_has_a_default() {
        var request = IntelligenceAnswer.Parse("anthropic")!.Request(model: null, apiKey: "k", importOutcomes: null);

        await Assert.That(request.Model).IsNull();
        await Assert.That(request.ApiKey).IsEqualTo("k");
        await Assert.That(request.Vendor).IsEqualTo("anthropic");
    }

    [Test]
    public async Task OpenAI_sends_its_default_model_since_the_server_has_none() =>
        await Assert.That(IntelligenceAnswer.Parse("openai")!.Request(null, "k", null).Model).IsEqualTo(IntelligenceVendor.OpenAI.DefaultModel);

    [Test]
    public async Task A_chosen_model_is_sent_as_given() =>
        await Assert.That(IntelligenceAnswer.Parse("cohere")!.Request(" command-r ", "k", null).Model).IsEqualTo("command-r");

    [Test]
    public async Task The_local_agent_and_none_send_neither_key_nor_model() {
        var local = IntelligenceAnswer.Parse("codex")!.Request("m", "k", true);
        var none  = IntelligenceAnswer.Parse("none")!.Request("m", "k", false);

        await Assert.That(local).IsEqualTo(new IntelligenceSetupRequest("local_agent", Harness: "codex", ImportOutcomes: true));
        await Assert.That(none).IsEqualTo(new IntelligenceSetupRequest("none", ImportOutcomes: false));
    }
}
