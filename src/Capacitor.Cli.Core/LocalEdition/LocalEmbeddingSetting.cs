namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>What setup recorded about semantic search: an Ollama model, the built-in model set, or a decline.
/// An installation with no record was never asked. <see cref="Model"/> is the Ollama model only.</summary>
public sealed record LocalEmbeddingSetting(LocalEmbeddingProvider Provider, string? Model, string? ModelSetId) {
    public static readonly LocalEmbeddingSetting Declined = new(LocalEmbeddingProvider.None, null, null);

    public static LocalEmbeddingSetting Ollama(string model) => new(LocalEmbeddingProvider.Ollama, model, null);

    public static LocalEmbeddingSetting Builtin(string modelSetId) => new(LocalEmbeddingProvider.Builtin, null, modelSetId);

    /// <summary>A provider this release does not know, kept so that it is never overwritten.</summary>
    public static readonly LocalEmbeddingSetting Unknown = new(LocalEmbeddingProvider.Unknown, null, null);

    /// <summary>Recorded, but the unit may not serve it yet: the next setup rewrites the unit whatever it decides.</summary>
    public bool Pending { get; init; }
}
