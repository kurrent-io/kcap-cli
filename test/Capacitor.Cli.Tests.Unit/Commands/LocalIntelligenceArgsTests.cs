using Capacitor.Cli.Commands;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>One answer, its model for a provider, at most one outcome flag, and never a key on the command line</summary>
public class LocalIntelligenceArgsTests {
    [Test]
    public async Task An_answer_parses_with_its_options() {
        var (args, error) = LocalIntelligenceArgs.Parse(["openai", "--model", "gpt-5", "--no-import-outcomes", "--json"]);

        await Assert.That(error).IsNull();
        await Assert.That(args!.Answer.Token).IsEqualTo("openai");
        await Assert.That(args.Model).IsEqualTo("gpt-5");
        await Assert.That(args.ImportOutcomes == false).IsTrue();
        await Assert.That(args.Json).IsTrue();
    }

    [Test]
    public async Task The_model_takes_the_equals_form_and_outcomes_default_to_unsaid() {
        var (args, _) = LocalIntelligenceArgs.Parse(["anthropic", "--model=claude-haiku-4-5"]);

        await Assert.That(args!.Model).IsEqualTo("claude-haiku-4-5");
        await Assert.That(args.ImportOutcomes).IsNull();
    }

    [Test]
    [Arguments(new string[0], "Name who runs")]
    [Arguments(new[] { "gemini" }, "'gemini' is not an answer")]
    [Arguments(new[] { "claude", "--model", "x" }, "--model is for a provider")]
    [Arguments(new[] { "anthropic", "--model" }, "--model needs a model name")]
    [Arguments(new[] { "claude", "--import-outcomes", "--no-import-outcomes" }, "contradict")]
    [Arguments(new[] { "claude", "--verbose" }, "Unknown option")]
    [Arguments(new[] { "anthropic", "sk-ant-secret" }, "never from the command line")]
    [Arguments(new[] { "claude", "codex" }, "one answer")]
    public async Task Anything_else_is_refused(string[] argv, string reason) {
        var (args, error) = LocalIntelligenceArgs.Parse(argv);

        await Assert.That(args).IsNull();
        await Assert.That(error).Contains(reason);
    }

    [Test]
    public async Task A_refused_key_is_never_echoed() {
        var (_, error) = LocalIntelligenceArgs.Parse(["anthropic", "sk-ant-secret"]);

        await Assert.That(error).DoesNotContain("sk-ant-secret");
        await Assert.That(error).Contains("ANTHROPIC_API_KEY");
    }
}
