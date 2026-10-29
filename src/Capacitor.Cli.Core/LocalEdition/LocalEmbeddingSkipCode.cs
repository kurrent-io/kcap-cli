namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>Why a local setup left semantic search off, or on the model it already had. None of these fails the
/// install: search then works by keyword.</summary>
public enum LocalEmbeddingSkipCode {
    Declined,
    OllamaNotFound,
    PullFailed,
    ModelUnusable,
    WriteFailed,
    UnrecognisedProvider,
    BuiltinUnavailable,
    ModelDownloadFailed
}

public static class LocalEmbeddingSkipCodeExtensions {
    /// <summary>The snake_case name that reaches events.</summary>
    public static string Wire(this LocalEmbeddingSkipCode code) => code switch {
        LocalEmbeddingSkipCode.Declined       => "declined",
        LocalEmbeddingSkipCode.OllamaNotFound => "ollama_not_found",
        LocalEmbeddingSkipCode.PullFailed     => "pull_failed",
        LocalEmbeddingSkipCode.ModelUnusable  => "model_unusable",
        LocalEmbeddingSkipCode.WriteFailed    => "write_failed",
        LocalEmbeddingSkipCode.UnrecognisedProvider => "unrecognised_provider",
        LocalEmbeddingSkipCode.BuiltinUnavailable   => "builtin_unavailable",
        LocalEmbeddingSkipCode.ModelDownloadFailed  => "model_download_failed",
        _                                     => throw new ArgumentOutOfRangeException(nameof(code), code, null)
    };
}
