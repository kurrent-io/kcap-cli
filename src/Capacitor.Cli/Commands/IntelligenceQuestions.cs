using Capacitor.Cli.Core.Setup.Intelligence;
using Spectre.Console;

namespace Capacitor.Cli.Commands;

/// <summary>The terminal's <see cref="IIntelligenceQuestions"/>.</summary>
sealed class IntelligenceQuestions : IIntelligenceQuestions {
    public const string Explanation =
        "kcap titles your sessions, says what each achieved and runs evals in the background. Something has to run "
      + "those model calls: your own coding agent on this machine, on its sign-in and quota, or a provider with your API key.";

    public string Choose(IReadOnlyList<string> harnesses) {
        AnsiConsole.MarkupLine($"  {Markup.Escape(Explanation)}");

        var choices = harnesses.Select(h => (Token: h, Label: $"My local agent: {HarnessLabel(h)}"))
            .Append((Token: LocalIntelligenceStep.ProviderChoice, Label: "A provider, with my API key"))
            .Append((Token: IntelligenceAnswer.NoneToken, Label: "Not now"))
            .ToList();

        var picked = AnsiConsole.Prompt(new SelectionPrompt<string>()
            .Title("Who runs background intelligence?")
            .AddChoices(choices.Select(c => c.Label)));

        return choices.First(c => c.Label == picked).Token;
    }

    public IntelligenceVendor Vendor() {
        var picked = AnsiConsole.Prompt(new SelectionPrompt<string>().Title("Which provider?").AddChoices(IntelligenceVendor.All.Select(v => v.Label)));

        return IntelligenceVendor.All.First(v => v.Label == picked);
    }

    public string Model(IntelligenceVendor vendor) =>
        AnsiConsole.Prompt(new TextPrompt<string>("Model:").DefaultValue(vendor.DefaultModel));

    public string? Key(IntelligenceVendor vendor) => LocalIntelligenceCommand.AskKey(vendor);

    public bool ImportOutcomes() =>
        AnsiConsole.Prompt(new ConfirmationPrompt("Also summarise what each imported session achieved?") { DefaultValue = true });

    public static string HarnessLabel(string harness) => harness switch {
        HeadlessHarnessProbe.Claude => "Claude Code",
        HeadlessHarnessProbe.Codex  => "Codex",
        _                           => harness,
    };
}
