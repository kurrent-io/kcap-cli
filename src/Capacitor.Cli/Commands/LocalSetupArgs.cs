using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Core.Setup.Intelligence;
using Capacitor.Cli.LocalEdition;

namespace Capacitor.Cli.Commands;

/// <summary>What <c>kcap setup --local</c> was asked. Every other step's flags pass through to the terminal arm
/// untouched; under <c>--json</c> alone nothing else is accepted, because the desktop app's wizard owns those steps.
/// <c>--json --no-prompt</c> is the whole run, reported.</summary>
sealed record LocalSetupArgs(
        bool Json, bool SingleUser, bool Yes, bool NoPrompt, string? EmbeddingModel = null, bool NoEmbeddings = false, bool WholeRun = false,
        bool BuiltinEmbeddings = false, IntelligenceAnswer? Intelligence = null, string? IntelligenceModel = null, bool? ImportOutcomes = null,
        bool WindowsService = false) {
    public const string EmbeddingModelFlag    = "--embedding-model";
    public const string NoEmbeddingsFlag      = "--no-embeddings";
    public const string BuiltinEmbeddingsFlag = "--builtin-embeddings";
    public const string WindowsServiceFlag    = "--windows-service";
    public const string IntelligenceFlag      = "--intelligence";
    public const string IntelligenceModelFlag = "--intelligence-model";

    static readonly string[] Conflicting = [
        "--server-url", "--discover", "--org", "--slug", "--tier", "--invite-code", "--device", "--github", "--muse-assistant",
    ];

    /// <summary>The flags whose next token is their value, which a bare name check must skip.</summary>
    static readonly string[] TakesValue = [
        "--plugin-scope", "--default-visibility", "--use-provider-api-key", "--daemon-name", EmbeddingModelFlag, IntelligenceFlag, IntelligenceModelFlag,
    ];

    public const string ImportOutcomesFlag   = "--import-outcomes";
    public const string NoImportOutcomesFlag = "--no-import-outcomes";

    static readonly string[] InstallConfirmations = ["--single-user", "--yes"];

    static readonly string[] ValueFlags = [EmbeddingModelFlag, IntelligenceFlag, IntelligenceModelFlag];

    static readonly string[] JsonAccepts = [
        "--local", "--json", "--single-user", "--yes", "--no-update-check", EmbeddingModelFlag, NoEmbeddingsFlag, BuiltinEmbeddingsFlag,
        WindowsServiceFlag,
    ];

    /// <summary>The embedding answer the flags give; <see cref="LocalEmbeddingAnswer.Default"/> when they give none.</summary>
    public LocalEmbeddingAnswer Embeddings =>
        NoEmbeddings ? new LocalEmbeddingAnswer.Skip()
      : BuiltinEmbeddings ? new LocalEmbeddingAnswer.Builtin()
      : EmbeddingModel is { } model ? new LocalEmbeddingAnswer.Ollama(model)
      : new LocalEmbeddingAnswer.Default();

    /// <param name="args">The command line with the verb at index 0, as <c>HandleAsync</c> receives it.</param>
    /// <param name="kcapUrlEnv">The <c>KCAP_URL</c> environment value.</param>
    /// <param name="isWindows">Test seam; production checks the running OS.</param>
    /// <param name="variable">Reads an environment variable; production reads the process's own.</param>
    public static (LocalSetupArgs? Args, string? Error) Parse(
            string[] args, string? kcapUrlEnv, bool? isWindows = null, Func<string, string?>? variable = null) {
        var rest = args.Skip(1).SelectMany(Split).ToArray();

        foreach (var arg in rest) {
            var flag = arg.Split('=', 2)[0];

            if (Conflicting.Contains(flag))
                return Refuse($"{flag} cannot be used with --local, which sets up its own server on this machine.");
        }

        string? model = null;
        string? intelligence = null;
        string? intelligenceModel = null;

        for (var i = 0; i < rest.Length; i++) {
            if (rest[i] is IntelligenceFlag or IntelligenceModelFlag) {
                if (i + 1 >= rest.Length || rest[i + 1].StartsWith('-'))
                    return Refuse(rest[i] == IntelligenceFlag
                        ? $"{IntelligenceFlag} needs who runs background intelligence: one of {string.Join(", ", IntelligenceAnswer.Tokens)}."
                        : $"{IntelligenceModelFlag} needs a model name.");

                if (rest[i] == IntelligenceFlag) intelligence = rest[i + 1];
                else intelligenceModel = rest[i + 1];
            }

            if (rest[i] == EmbeddingModelFlag) {
                if (i + 1 >= rest.Length || !LocalEmbeddingModelName.IsValid(rest[i + 1]))
                    return Refuse($"{EmbeddingModelFlag} needs an Ollama model name, such as {OllamaClient.DefaultModel}.");

                model = rest[i + 1];
            }

            if (TakesValue.Contains(rest[i])) {
                i++;
                continue;
            }

            if (!rest[i].StartsWith('-'))
                return Refuse($"'{rest[i]}' names a server, and --local sets up its own on this machine. Drop one of them.");
        }

        var noEmbeddings = rest.Contains(NoEmbeddingsFlag);
        var builtin      = rest.Contains(BuiltinEmbeddingsFlag);

        var given = new List<string>();
        if (builtin) given.Add(BuiltinEmbeddingsFlag);
        if (noEmbeddings) given.Add(NoEmbeddingsFlag);
        if (model is not null) given.Add(EmbeddingModelFlag);

        if (given.Count > 1)
            return Refuse($"{given[0]} and {given[1]} contradict each other. Drop one of them.");

        var windowsService = rest.Contains(WindowsServiceFlag);

        if (windowsService && !(isWindows ?? OperatingSystem.IsWindows()))
            return Refuse($"{WindowsServiceFlag} runs the local server under the Windows service; it does nothing on this OS.");

        if (!string.IsNullOrEmpty(kcapUrlEnv))
            return Refuse($"KCAP_URL is set ({kcapUrlEnv}), so every later step would talk to that server instead of the local one. Unset it to use --local.");

        var json     = rest.Contains("--json");
        var noPrompt = rest.Contains("--no-prompt");

        IntelligenceAnswer? answer = null;

        if (intelligence is not null && (answer = IntelligenceAnswer.Parse(intelligence)) is null)
            return Refuse($"'{intelligence}' is not an answer for {IntelligenceFlag}; use one of {string.Join(", ", IntelligenceAnswer.Tokens)}.");

        if (intelligenceModel is not null && answer?.Vendor is null)
            return Refuse($"{IntelligenceModelFlag} is for a provider named by {IntelligenceFlag}.");

        if (rest.Contains(ImportOutcomesFlag) && rest.Contains(NoImportOutcomesFlag))
            return Refuse($"{ImportOutcomesFlag} and {NoImportOutcomesFlag} contradict each other. Drop one of them.");

        // Asked before anything runs: a run that cannot ask would otherwise install a server and leave this unset.
        if (noPrompt && answer is null) {
            var alsoMissing = InstallConfirmations.Where(f => !rest.Contains(f)).ToList();

            return Refuse($"--no-prompt needs {IntelligenceFlag} <answer>: who runs background intelligence, one of "
                        + $"{string.Join(", ", IntelligenceAnswer.Tokens)}."
                        + (alsoMissing.Count == 0 ? "" : $" Pass {string.Join(" and ", alsoMissing)} too, to install."));
        }

        if (noPrompt && answer?.Vendor is { } vendor && string.IsNullOrWhiteSpace((variable ?? Environment.GetEnvironmentVariable)(vendor.KeyVariable)))
            return Refuse($"{IntelligenceFlag} {vendor.Id} needs the API key in {vendor.KeyVariable} when setup cannot ask for it.");

        bool? importOutcomes = rest.Contains(ImportOutcomesFlag) ? true : rest.Contains(NoImportOutcomesFlag) ? false : null;

        if (json && !noPrompt && Flags(rest).FirstOrDefault(a => !JsonAccepts.Contains(a)) is { } extra)
            return Refuse($"{extra} cannot be used with --local --json, which runs only the install and accepts only --single-user, --yes, "
                        + $"--no-update-check, {EmbeddingModelFlag}, {NoEmbeddingsFlag}, {BuiltinEmbeddingsFlag} and {WindowsServiceFlag}. Add --no-prompt to run every step.");

        return (new(json, rest.Contains("--single-user"), rest.Contains("--yes"), json || noPrompt, model, noEmbeddings, json && noPrompt, builtin,
            answer, intelligenceModel?.Trim(), importOutcomes, windowsService), null);
    }

    /// <summary>The <c>--flag=&lt;value&gt;</c> form of a flag taking a value, as two tokens.</summary>
    static string[] Split(string arg) =>
        ValueFlags.FirstOrDefault(f => arg.StartsWith(f + "=", StringComparison.Ordinal)) is { } flag
            ? [flag, arg[(flag.Length + 1)..]]
            : [arg];

    /// <summary>Every token but the values of the flags that take one.</summary>
    static IEnumerable<string> Flags(string[] rest) {
        for (var i = 0; i < rest.Length; i++) {
            yield return rest[i];

            if (rest[i] is EmbeddingModelFlag or IntelligenceFlag or IntelligenceModelFlag) i++;
        }
    }

    static (LocalSetupArgs?, string?) Refuse(string error) => (null, error);
}
