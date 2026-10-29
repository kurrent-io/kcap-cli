using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.LocalEdition;

/// <summary>What the user agreed to. The first two are required for a new installation and ignored for a reuse.</summary>
/// <param name="SingleUser">The user accepted that the local edition serves only them.</param>
/// <param name="Confirmed">The user confirmed the install.</param>
/// <param name="Embeddings">Null leaves the embedding configuration as it is, without looking for Ollama.</param>
/// <param name="BuiltinModel">The built-in model set the installed version offers, from
/// <see cref="LocalInstaller.FetchEmbeddingManifestAsync"/>; null when it offers none or it could not be fetched.</param>
sealed record LocalInstallAnswers(bool SingleUser, bool Confirmed, LocalEmbeddingAnswer? Embeddings = null, LocalEmbeddingModelManifest? BuiltinModel = null);
