using System.Text.Json;
using System.Text.Json.Serialization;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.LocalEdition;

/// <summary>
/// <c>data/embedding.json</c>: the embedding model setup chose, or that the user declined one. A model is what
/// <see cref="LocalServerUnit"/> renders into the server's environment; a decline is kept so that a later setup which
/// asks nothing does not undo it. It lives under <c>data/</c> because it describes the vectors stored there: a rollback
/// or a purge that deletes them deletes it too, and an uninstall that keeps them keeps it.
/// </summary>
static partial class LocalEmbeddingFile {
    const string OllamaProvider = "ollama";
    const string NoProvider     = "none";

    const UnixFileMode OwnerOnly      = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static string PathFor(LocalRoot root) => Path.Combine(root.Directory, "data", "embedding.json");

    /// <summary>The recorded setting, or null when there is none. An unreadable file counts as none: the server then
    /// searches by keyword, and the next setup writes it again.</summary>
    public static LocalEmbeddingSetting? Read(LocalRoot root) {
        try {
            return JsonSerializer.Deserialize(File.ReadAllBytes(PathFor(root)), FileJsonContext.Default.Wire) switch {
                { Provider: OllamaProvider, Model: { Length: > 0 } model } => new(model),
                { Provider: NoProvider }                                   => LocalEmbeddingSetting.Declined,
                _                                                          => null,
            };
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            return null;
        }
    }

    public static string? ReadModel(LocalRoot root) => Read(root)?.Model;

    /// <summary>Durable and owner-only, creating <c>data/</c> owner-only if this is the first thing in it.</summary>
    public static void Write(LocalRoot root, LocalEmbeddingSetting setting) {
        var path = PathFor(root);

        DurableDirectory.Ensure(Path.GetDirectoryName(path)!, root.Directory, OperatingSystem.IsWindows() ? null : OwnerOnly);

        var wire  = setting.Model is { } model ? new Wire { Provider = OllamaProvider, Model = model } : new Wire { Provider = NoProvider };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(wire, FileJsonContext.Default.Wire);
        AtomicFile.Replace(path, bytes, OperatingSystem.IsWindows() ? null : OwnerReadWrite, followLink: false, durable: true);
    }

    internal sealed class Wire {
        public string? Provider { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Model { get; set; }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
    [JsonSerializable(typeof(Wire))]
    internal partial class FileJsonContext : JsonSerializerContext;
}
