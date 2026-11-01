using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;

namespace Capacitor.Cli.Commands;

/// <summary>What <c>kcap setup --local</c> was asked. Every other step's flags pass through to the terminal arm
/// untouched; under <c>--json</c> alone nothing else is accepted, because the desktop app's wizard owns those steps.
/// <c>--json --no-prompt</c> is the whole run, reported.</summary>
sealed record LocalSetupArgs(
        bool Json, bool SingleUser, bool Yes, bool NoPrompt, string? EmbeddingModel = null, bool NoEmbeddings = false, bool WholeRun = false,
        bool BuiltinEmbeddings = false, bool WindowsService = false) {
    public const string EmbeddingModelFlag    = "--embedding-model";
    public const string NoEmbeddingsFlag      = "--no-embeddings";
    public const string BuiltinEmbeddingsFlag = "--builtin-embeddings";
    public const string WindowsServiceFlag    = "--windows-service";

    static readonly string[] Conflicting = [
        "--server-url", "--discover", "--org", "--slug", "--tier", "--invite-code", "--device", "--github", "--muse-assistant",
    ];

    /// <summary>The flags whose next token is their value, which a bare name check must skip.</summary>
    static readonly string[] TakesValue = ["--plugin-scope", "--default-visibility", "--use-provider-api-key", "--daemon-name", EmbeddingModelFlag];

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
    public static (LocalSetupArgs? Args, string? Error) Parse(string[] args, string? kcapUrlEnv, bool? isWindows = null) {
        var rest = args.Skip(1).SelectMany(Split).ToArray();

        foreach (var arg in rest) {
            var flag = arg.Split('=', 2)[0];

            if (Conflicting.Contains(flag))
                return Refuse($"{flag} cannot be used with --local, which sets up its own server on this machine.");
        }

        string? model = null;

        for (var i = 0; i < rest.Length; i++) {
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

        if (json && !noPrompt && Flags(rest).FirstOrDefault(a => !JsonAccepts.Contains(a)) is { } extra)
            return Refuse($"{extra} cannot be used with --local --json, which runs only the install and accepts only --single-user, --yes, "
                        + $"--no-update-check, {EmbeddingModelFlag}, {NoEmbeddingsFlag}, {BuiltinEmbeddingsFlag} and {WindowsServiceFlag}. Add --no-prompt to run every step.");

        return (new(json, rest.Contains("--single-user"), rest.Contains("--yes"), json || noPrompt, model, noEmbeddings, json && noPrompt, builtin,
            windowsService), null);
    }

    /// <summary>The <c>--embedding-model=&lt;model&gt;</c> form as two tokens.</summary>
    static string[] Split(string arg) =>
        arg.StartsWith(EmbeddingModelFlag + "=", StringComparison.Ordinal) ? [EmbeddingModelFlag, arg[(EmbeddingModelFlag.Length + 1)..]] : [arg];

    /// <summary>Every token but the value of <see cref="EmbeddingModelFlag"/>.</summary>
    static IEnumerable<string> Flags(string[] rest) {
        for (var i = 0; i < rest.Length; i++) {
            yield return rest[i];

            if (rest[i] == EmbeddingModelFlag) i++;
        }
    }

    static (LocalSetupArgs?, string?) Refuse(string error) => (null, error);
}
