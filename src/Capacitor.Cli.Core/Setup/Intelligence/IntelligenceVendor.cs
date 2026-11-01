namespace Capacitor.Cli.Core.Setup.Intelligence;

/// <summary>A model provider the server can run background work on with the user's own key.</summary>
/// <param name="Id">The server's vendor name, also the answer's token.</param>
/// <param name="KeyVariable">Where the key is read from; a key is never taken from the command line, where any local
/// user can read it.</param>
/// <param name="DefaultModel">The server's own default for its evals, offered as the model to run.</param>
/// <param name="ServerHasDefault">False where the server has no default model for some feature, so a model must be
/// sent.</param>
public sealed record IntelligenceVendor(string Id, string Label, string KeyVariable, string DefaultModel, bool ServerHasDefault) {
    public static readonly IntelligenceVendor Anthropic  = new("anthropic", "Anthropic", "ANTHROPIC_API_KEY", "claude-sonnet-4-6", true);
    public static readonly IntelligenceVendor OpenAI     = new("openai", "OpenAI", "OPENAI_API_KEY", "gpt-5-mini", false);
    public static readonly IntelligenceVendor OpenRouter = new("openrouter", "OpenRouter", "OPENROUTER_API_KEY", "google/gemini-2.5-flash", true);
    public static readonly IntelligenceVendor Cohere     = new("cohere", "Cohere", "COHERE_API_KEY", "command-a-03-2025", true);

    public static IReadOnlyList<IntelligenceVendor> All { get; } = [Anthropic, OpenAI, OpenRouter, Cohere];
}
