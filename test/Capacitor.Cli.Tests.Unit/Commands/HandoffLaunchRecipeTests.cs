using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class HandoffLaunchRecipeTests {
    const string P = "Follow my kcap import\n(run: 0123456789abcdef0123456789abcdef)";

    [Test]
    [Arguments(HarnessId.Claude,      new[] { P })]
    [Arguments(HarnessId.Codex,       new[] { P })]
    [Arguments(HarnessId.Cursor,      new[] { P })]
    [Arguments(HarnessId.Copilot,     new[] { "-i", P })]
    [Arguments(HarnessId.Gemini,      new[] { "-i", P })]
    [Arguments(HarnessId.Kiro,        new[] { "chat", P })]
    [Arguments(HarnessId.Pi,          new[] { P })]
    [Arguments(HarnessId.OpenCode,    new[] { "--prompt", P })]
    [Arguments(HarnessId.Antigravity, new[] { "-i", P })]
    public async Task Every_vendor_has_a_recipe_and_the_prompt_is_one_argument(HarnessId vendor, string[] expected) {
        var argv = HandoffLaunchRecipe.All[vendor].Argv(P).ToList();

        await Assert.That(argv).IsEquivalentTo(expected);
        await Assert.That(argv.Last()).IsEqualTo(P);
    }

    [Test]
    public async Task The_registry_covers_every_harness() =>
        await Assert.That(HandoffLaunchRecipe.All.Keys.ToList()).IsEquivalentTo(HarnessRegistry.Identities.Select(i => i.Id).ToList());
}
