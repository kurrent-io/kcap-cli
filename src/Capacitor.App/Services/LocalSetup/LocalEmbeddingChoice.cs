using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.App.Services.LocalSetup;

/// What a setup run is told about search by meaning: an Ollama model, the built-in model, or keyword search only. The
/// model fixes the width of the stored vectors, so a different one later means embedding everything again.
public sealed record LocalEmbeddingChoice(string? Model) {
    public const string DefaultModel = "mxbai-embed-large";

    public static readonly LocalEmbeddingChoice KeywordOnly = new((string?)null);

    public static readonly LocalEmbeddingChoice Default = new(DefaultModel);

    public static readonly LocalEmbeddingChoice Builtin = new((string?)null) { Provider = LocalEmbeddingProvider.Builtin };

    /// Nobody chose, so the run passes no flag and setup decides: Ollama when it runs, else the built-in model when the
    /// stack offers it, else keyword search, recording nothing. <paramref name="expected"/> is what this side expects.
    public static LocalEmbeddingChoice Recommended(LocalEmbeddingChoice expected) => expected with { Unchosen = true };

    public LocalEmbeddingProvider Provider { get; private init; } = Model is null ? LocalEmbeddingProvider.None : LocalEmbeddingProvider.Ollama;

    public bool Unchosen { get; private init; }

    public bool ByMeaning => Provider != LocalEmbeddingProvider.None;

    public string[] Args =>
        Unchosen ? []
      : Provider == LocalEmbeddingProvider.Builtin ? ["--builtin-embeddings"]
      : Model is { } model ? ["--embedding-model", model]
      : ["--no-embeddings"];
}
