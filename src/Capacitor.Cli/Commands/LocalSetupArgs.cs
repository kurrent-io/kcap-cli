using System.Text.RegularExpressions;
using Capacitor.Cli.LocalEdition;

namespace Capacitor.Cli.Commands;

/// <summary>What <c>kcap setup --local</c> was asked. Every other step's flags pass through to the terminal arm
/// untouched; under <c>--json</c> nothing else is accepted, because the wizard owns those steps.</summary>
sealed partial record LocalSetupArgs(bool Json, bool SingleUser, bool Yes, bool NoPrompt, string? EmbeddingModel = null, bool NoEmbeddings = false) {
    public const string EmbeddingModelFlag = "--embedding-model";
    public const string NoEmbeddingsFlag   = "--no-embeddings";

    static readonly string[] Conflicting = [
        "--server-url", "--discover", "--org", "--slug", "--tier", "--invite-code", "--device", "--github", "--muse-assistant",
    ];

    /// <summary>The flags whose next token is their value, which a bare name check must skip.</summary>
    static readonly string[] TakesValue = ["--plugin-scope", "--default-visibility", "--use-provider-api-key", "--daemon-name", EmbeddingModelFlag];

    static readonly string[] JsonAccepts = ["--local", "--json", "--single-user", "--yes", "--no-update-check", EmbeddingModelFlag, NoEmbeddingsFlag];

    /// <summary>The embedding answer the flags give, or null when they give none.</summary>
    public LocalEmbeddingAnswer? Embeddings =>
        NoEmbeddings ? new LocalEmbeddingAnswer.Skip()
      : EmbeddingModel is { } model ? new LocalEmbeddingAnswer.Ollama(model)
      : null;

    /// <param name="args">The command line with the verb at index 0, as <c>HandleAsync</c> receives it.</param>
    /// <param name="kcapUrlEnv">The <c>KCAP_URL</c> environment value.</param>
    public static (LocalSetupArgs? Args, string? Error) Parse(string[] args, string? kcapUrlEnv) {
        var rest = args.Skip(1).ToArray();

        foreach (var arg in rest) {
            var flag = arg.Split('=', 2)[0];

            if (Conflicting.Contains(flag))
                return Refuse($"{flag} cannot be used with --local, which sets up its own server on this machine.");
        }

        string? model = null;

        for (var i = 0; i < rest.Length; i++) {
            if (rest[i] == EmbeddingModelFlag) {
                if (i + 1 >= rest.Length || !ModelName().IsMatch(rest[i + 1]))
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

        if (noEmbeddings && model is not null)
            return Refuse($"{NoEmbeddingsFlag} and {EmbeddingModelFlag} contradict each other. Drop one of them.");

        if (!string.IsNullOrEmpty(kcapUrlEnv))
            return Refuse($"KCAP_URL is set ({kcapUrlEnv}), so every later step would talk to that server instead of the local one. Unset it to use --local.");

        var json = rest.Contains("--json");

        if (json && Flags(rest).FirstOrDefault(a => !JsonAccepts.Contains(a)) is { } extra)
            return Refuse($"{extra} cannot be used with --local --json, which accepts only --single-user, --yes, --no-update-check, "
                        + $"{EmbeddingModelFlag} and {NoEmbeddingsFlag}.");

        return (new(json, rest.Contains("--single-user"), rest.Contains("--yes"), json || rest.Contains("--no-prompt"), model, noEmbeddings), null);
    }

    /// <summary>Every token but the value of <see cref="EmbeddingModelFlag"/>.</summary>
    static IEnumerable<string> Flags(string[] rest) {
        for (var i = 0; i < rest.Length; i++) {
            yield return rest[i];

            if (rest[i] == EmbeddingModelFlag) i++;
        }
    }

    static (LocalSetupArgs?, string?) Refuse(string error) => (null, error);

    public static bool IsModelName(string name) => ModelName().IsMatch(name);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:/-]{0,199}$")]
    private static partial Regex ModelName();
}
