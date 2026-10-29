using System.Text.RegularExpressions;

namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>The Ollama model names <c>kcap setup --local --embedding-model</c> accepts.</summary>
public static partial class LocalEmbeddingModelName {
    public static bool IsValid(string name) => Pattern().IsMatch(name);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:/-]{0,199}$")]
    private static partial Regex Pattern();
}
