namespace Capacitor.App.Services.LocalSetup;

/// What a setup run is told about search by meaning: an Ollama model, or keyword search only. The model fixes
/// the width of the stored vectors, so a different one later means embedding everything again.
public sealed record LocalEmbeddingChoice(string? Model) {
    public const string DefaultModel = "mxbai-embed-large";

    public static readonly LocalEmbeddingChoice KeywordOnly = new((string?)null);

    public static readonly LocalEmbeddingChoice Default = new(DefaultModel);

    public string[] Args => Model is { } model ? ["--embedding-model", model] : ["--no-embeddings"];
}
