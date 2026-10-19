namespace Capacitor.Cli.LocalEdition;

/// <summary>What setup recorded about semantic search: an Ollama <paramref name="Model"/>, or null for a user who
/// declined it. An installation with no record was never asked.</summary>
sealed record LocalEmbeddingSetting(string? Model) {
    public static readonly LocalEmbeddingSetting Declined = new((string?)null);
}
