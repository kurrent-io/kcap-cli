namespace Capacitor.Cli.LocalEdition;

/// <summary>Ollama answered, but not with what the call needed. The message is Ollama's own where it gave one.</summary>
sealed class OllamaException(string message, Exception? inner = null) : Exception(message, inner);
