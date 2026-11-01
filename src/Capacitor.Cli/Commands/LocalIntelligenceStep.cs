using Capacitor.Cli.Core.Setup.Intelligence;

namespace Capacitor.Cli.Commands;

/// <summary>
/// <c>kcap setup --local</c>'s background intelligence question. A flag answers it; otherwise a person is asked until
/// the server takes an answer or they choose not now, which stores nothing, so a re-run never clears what an earlier
/// one chose. A refused or unreachable answer never fails setup: it says how to answer later.
/// </summary>
/// <param name="questions">Null when nobody can be asked.</param>
/// <param name="say">One line for the person, unformatted.</param>
sealed class LocalIntelligenceStep(
        Func<IntelligenceSetupRequest, Task<IntelligenceSetupOutcome>> apply,
        Func<Task<string[]>> harnesses,
        Func<string, string?> variable,
        IIntelligenceQuestions? questions,
        Action<string> say) {
    public const string ProviderChoice = "provider";

    const string Later = "kcap local intelligence <answer> sets it up later.";

    /// <returns>The answer as the server applied it, or null when none was.</returns>
    public async Task<IntelligenceSetupResponse?> RunAsync(LocalSetupArgs args) {
        if (args.Intelligence is { } given) {
            var (key, error) = given.Vendor is { } vendor
                ? IntelligenceKey.Resolve(vendor, variable, questions is null ? null : () => questions.Key(vendor))
                : (null, null);

            if (error is not null) return Unset(error);

            return await ApplyAsync(given.Request(args.IntelligenceModel, key, args.ImportOutcomes)) switch {
                IntelligenceSetupOutcome.Applied { Response: var applied } => Applied(applied),
                IntelligenceSetupOutcome.Refused { Error: var code }       => Unset(IntelligenceRefusals.Describe(code)),
                IntelligenceSetupOutcome.Failed { Problem: var problem }   => Unset($"Could not reach the local server: {problem}"),
                _                                                          => null,
            };
        }

        if (questions is null) return Unset("Background intelligence: not chosen.");

        var offered = await harnesses();

        while (true) {
            var choice = questions.Choose(offered);

            if (choice == IntelligenceAnswer.NoneToken) {
                say($"Background intelligence: not set up. {Later}");

                return null;
            }

            IntelligenceAnswer answer;
            string? model = null, key = null;

            if (choice == ProviderChoice) {
                var vendor = questions.Vendor();
                model = questions.Model(vendor);
                (key, var error) = IntelligenceKey.Resolve(vendor, variable, () => questions.Key(vendor));

                if (error is not null) {
                    say(error);

                    continue;
                }

                answer = IntelligenceAnswer.Provider(vendor);
            } else {
                answer = IntelligenceAnswer.LocalAgent(choice);
            }

            var outcomes = args.ImportOutcomes ?? questions.ImportOutcomes();

            switch (await ApplyAsync(answer.Request(model, key, outcomes))) {
                case IntelligenceSetupOutcome.Applied { Response: var applied }:
                    return Applied(applied);
                case IntelligenceSetupOutcome.Refused { Error: var code }:
                    say(IntelligenceRefusals.Describe(code));

                    continue;
                case IntelligenceSetupOutcome.Failed { Problem: var problem }:
                    return Unset($"Could not reach the local server: {problem}");
            }
        }
    }

    Task<IntelligenceSetupOutcome> ApplyAsync(IntelligenceSetupRequest request) => apply(request);

    IntelligenceSetupResponse Applied(IntelligenceSetupResponse applied) {
        foreach (var line in LocalIntelligenceCommand.Summary(applied)) say(line);

        return applied;
    }

    IntelligenceSetupResponse? Unset(string why) {
        say($"{why} {Later}");

        return null;
    }
}
