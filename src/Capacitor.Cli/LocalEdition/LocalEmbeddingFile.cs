using System.Text.Json;
using System.Text.Json.Serialization;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.LocalEdition;

/// <summary>
/// <c>data/embedding.json</c>: the embedding model setup chose, which <see cref="LocalServerUnit"/> renders into the
/// server's environment. It lives under <c>data/</c> because it describes the vectors stored there: a rollback or a
/// purge that deletes them deletes it too, and an uninstall that keeps them keeps it.
/// </summary>
static partial class LocalEmbeddingFile {
    const string OllamaProvider = "ollama";

    const UnixFileMode OwnerOnly      = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static string PathFor(LocalRoot root) => Path.Combine(root.Directory, "data", "embedding.json");

    /// <summary>The configured Ollama model, or null for none. An unreadable file counts as none: the server then
    /// searches by keyword, and the next setup writes it again.</summary>
    public static string? ReadModel(LocalRoot root) {
        try {
            var wire = JsonSerializer.Deserialize(File.ReadAllBytes(PathFor(root)), FileJsonContext.Default.Wire);

            return wire is { Provider: OllamaProvider, Model: { Length: > 0 } model } ? model : null;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            return null;
        }
    }

    /// <summary>Durable and owner-only, creating <c>data/</c> owner-only if this is the first thing in it.</summary>
    public static void Write(LocalRoot root, string model) {
        var path = PathFor(root);

        DurableDirectory.Ensure(Path.GetDirectoryName(path)!, root.Directory, OperatingSystem.IsWindows() ? null : OwnerOnly);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Wire { Provider = OllamaProvider, Model = model }, FileJsonContext.Default.Wire);
        AtomicFile.Replace(path, bytes, OperatingSystem.IsWindows() ? null : OwnerReadWrite, followLink: false, durable: true);
    }

    public static void Delete(LocalRoot root) {
        var path = PathFor(root);

        if (!File.Exists(path)) return;

        File.Delete(path);
        DurableDirectory.Sync(Path.GetDirectoryName(path)!);
    }

    internal sealed class Wire {
        public string? Provider { get; set; }
        public string? Model    { get; set; }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
    [JsonSerializable(typeof(Wire))]
    internal partial class FileJsonContext : JsonSerializerContext;
}
