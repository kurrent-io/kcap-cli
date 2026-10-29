using System.Text.RegularExpressions;

namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>One file of the built-in embedding model set, as the signed manifest declares it.</summary>
public sealed partial record LocalEmbeddingModelFile(string Name, string Sha256, long Size) {
    /// <summary>A bare file name, never a path: it becomes both a URL segment and a file under the model directory.</summary>
    public static bool IsSafeName(string? name) => name is not null && NamePattern().IsMatch(name);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex NamePattern();
}
