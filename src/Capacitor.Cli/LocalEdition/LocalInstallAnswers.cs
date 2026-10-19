namespace Capacitor.Cli.LocalEdition;

/// <summary>What the user agreed to. The first two are required for a new installation and ignored for a reuse.</summary>
/// <param name="SingleUser">The user accepted that the local edition serves only them.</param>
/// <param name="Confirmed">The user confirmed the install.</param>
/// <param name="Embeddings">Null leaves the embedding configuration as it is, without looking for Ollama.</param>
sealed record LocalInstallAnswers(bool SingleUser, bool Confirmed, LocalEmbeddingAnswer? Embeddings = null);
