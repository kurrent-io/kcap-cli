namespace Capacitor.Cli.LocalEdition;

/// <summary>What setup does about semantic search.</summary>
abstract record LocalEmbeddingAnswer {
    LocalEmbeddingAnswer() { }

    /// <summary>Embed with Ollama when it runs here. A null <paramref name="Model"/> keeps the configured model, or
    /// takes <see cref="OllamaClient.DefaultModel"/> when there is none.</summary>
    public sealed record Ollama(string? Model) : LocalEmbeddingAnswer;

    /// <summary>No embeddings: search works by keyword, and a configured model is removed.</summary>
    public sealed record Skip : LocalEmbeddingAnswer;
}
