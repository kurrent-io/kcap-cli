namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>What setup recorded about semantic search: an Ollama <paramref name="Model"/>, or null for a user who
/// declined it. An installation with no record was never asked.</summary>
public sealed record LocalEmbeddingSetting(string? Model) {
    public static readonly LocalEmbeddingSetting Declined = new((string?)null);

    /// <summary>Recorded, but the unit may not serve it yet: the next setup rewrites the unit whatever it decides.</summary>
    public bool Pending { get; init; }
}
