namespace Capacitor.Cli.LocalEdition;

/// <summary>What the server's <c>/healthz/embeddings</c> says about the configured embedding provider.</summary>
/// <param name="Provider">The configured provider token, such as <c>builtin</c> or <c>ollama</c>.</param>
/// <param name="Usable">False for a built-in model that cannot embed.</param>
/// <param name="Reason">Why it cannot; null when usable or not yet loaded.</param>
sealed record EmbeddingHealthAnswer(string Provider, bool Usable, string? Reason);
