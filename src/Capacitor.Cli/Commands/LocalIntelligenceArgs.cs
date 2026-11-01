using Capacitor.Cli.Core.Setup.Intelligence;

namespace Capacitor.Cli.Commands;

/// <summary>What <c>kcap local intelligence</c> was asked.</summary>
/// <param name="ImportOutcomes">Null leaves the server's choice as it is.</param>
sealed record LocalIntelligenceArgs(IntelligenceAnswer Answer, string? Model, bool? ImportOutcomes, bool Json) {
    public const string ModelFlag             = "--model";
    public const string ImportOutcomesFlag    = "--import-outcomes";
    public const string NoImportOutcomesFlag  = "--no-import-outcomes";
    public const string JsonFlag              = "--json";

    static readonly string[] Switches = [ImportOutcomesFlag, NoImportOutcomesFlag, JsonFlag, "--no-update-check"];

    /// <param name="rest">The arguments after <c>local intelligence</c>.</param>
    public static (LocalIntelligenceArgs? Args, string? Error) Parse(string[] rest) {
        string? token = null;
        string? model = null;
        var     extra = false;

        for (var i = 0; i < rest.Length; i++) {
            var arg = rest[i];

            if (arg.StartsWith(ModelFlag + "=", StringComparison.Ordinal)) {
                model = arg[(ModelFlag.Length + 1)..];
            } else if (arg == ModelFlag) {
                if (i + 1 >= rest.Length || rest[i + 1].StartsWith('-')) return Refuse($"{ModelFlag} needs a model name.");
                model = rest[++i];
            } else if (arg.StartsWith('-')) {
                if (!Switches.Contains(arg)) return Refuse($"Unknown option for kcap local intelligence: {arg}");
            } else if (token is null) {
                token = arg;
            } else {
                extra = true;
            }
        }

        if (token is null)
            return Refuse($"Name who runs background intelligence: one of {string.Join(", ", IntelligenceAnswer.Tokens)}.");

        if (IntelligenceAnswer.Parse(token) is not { } answer)
            return Refuse($"'{token}' is not an answer; use one of {string.Join(", ", IntelligenceAnswer.Tokens)}.");

        // A second word is most likely a key pasted where it would sit in the shell history and `ps`.
        if (extra)
            return Refuse(answer.Vendor is { } keyed
                ? $"kcap local intelligence takes one answer. An API key is read from {keyed.KeyVariable} or a prompt, never from the command line."
                : "kcap local intelligence takes one answer.");

        if (model is not null && answer.Vendor is null) return Refuse($"{ModelFlag} is for a provider; {answer.Token} chooses its own model.");
        if (model is not null && string.IsNullOrWhiteSpace(model)) return Refuse($"{ModelFlag} needs a model name.");

        if (rest.Contains(ImportOutcomesFlag) && rest.Contains(NoImportOutcomesFlag))
            return Refuse($"{ImportOutcomesFlag} and {NoImportOutcomesFlag} contradict each other. Drop one of them.");

        bool? outcomes = rest.Contains(ImportOutcomesFlag) ? true : rest.Contains(NoImportOutcomesFlag) ? false : null;

        return (new(answer, model?.Trim(), outcomes, rest.Contains(JsonFlag)), null);
    }

    static (LocalIntelligenceArgs?, string?) Refuse(string error) => (null, error);
}
