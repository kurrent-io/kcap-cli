using System.Text.Json;
using System.Text.Json.Serialization;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>
/// <c>data/embedding.json</c>: the embedding model setup chose, or that the user declined one. A model is what the
/// server's service passes to it in its environment; a decline is kept so that a later setup which
/// asks nothing does not undo it. It lives under <c>data/</c> because it describes the vectors stored there: a rollback
/// or a purge that deletes them deletes it too, and an uninstall that keeps them keeps it.
/// </summary>
public static partial class LocalEmbeddingFile {
    const string OllamaProvider = "ollama";
    const string NoProvider     = "none";

    const UnixFileMode OwnerOnly      = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    public static string PathFor(LocalRoot root) => Path.Combine(root.Directory, "data", "embedding.json");

    /// <summary>The recorded setting, or null when there is none. An unreadable file counts as none: the server then
    /// searches by keyword, and the next setup writes it again.</summary>
    // Compatibility constraint: CLIs that predate the built-in provider read any provider they do not know as no setting
    // and then overwrite it with Ollama, and their writer drops fields they do not know. Built-in is therefore recorded
    // as "provider": "none" (an explicit, non-pending decline to them) plus a "builtin" object only this reader
    // understands, whose own "pending" stands in for the top-level flag those CLIs would act on.
    public static LocalEmbeddingSetting? Read(LocalRoot root) {
        try {
            return JsonSerializer.Deserialize(File.ReadAllBytes(PathFor(root)), FileJsonContext.Default.Wire) switch {
                { Provider: OllamaProvider, Model: { Length: > 0 } model } wire => LocalEmbeddingSetting.Ollama(model) with { Pending = wire.Pending },
                { Provider: OllamaProvider }                                    => null,
                { Provider: NoProvider, Builtin: { ModelSet: { Length: > 0 } set } builtin } => LocalEmbeddingSetting.Builtin(set) with { Pending = builtin.Pending },
                { Provider: NoProvider } wire                                   => LocalEmbeddingSetting.Declined with { Pending = wire.Pending },
                { Provider: { Length: > 0 } }                                   => LocalEmbeddingSetting.Unknown,
                _                                                               => null,
            };
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            return null;
        }
    }

    /// <summary>Durable and owner-only, creating <c>data/</c> owner-only if this is the first thing in it. Returns false,
    /// writing nothing, when the file records a provider this release does not know or <paramref name="setting"/> is one.</summary>
    public static bool Write(LocalRoot root, LocalEmbeddingSetting setting) {
        if (setting.Provider == LocalEmbeddingProvider.Unknown || Read(root)?.Provider == LocalEmbeddingProvider.Unknown) return false;

        var path = PathFor(root);

        DurableDirectory.Ensure(Path.GetDirectoryName(path)!, root.Directory, OperatingSystem.IsWindows() ? null : OwnerOnly);

        var wire = setting.Provider switch {
            LocalEmbeddingProvider.Ollama  => new Wire { Provider = OllamaProvider, Model = setting.Model, Pending = setting.Pending },
            LocalEmbeddingProvider.Builtin => new Wire { Provider = NoProvider, Builtin = new() { ModelSet = setting.ModelSetId, Pending = setting.Pending } },
            _                              => new Wire { Provider = NoProvider, Pending = setting.Pending },
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(wire, FileJsonContext.Default.Wire);
        AtomicFile.Replace(path, bytes, OperatingSystem.IsWindows() ? null : OwnerReadWrite, followLink: false, durable: true);

        return true;
    }

    public static void Delete(LocalRoot root) {
        var path = PathFor(root);

        if (!File.Exists(path)) return;

        File.Delete(path);
        DurableDirectory.Sync(Path.GetDirectoryName(path)!);
    }

    internal sealed class Wire {
        public string? Provider { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Model { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Pending { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public BuiltinWire? Builtin { get; set; }
    }

    internal sealed class BuiltinWire {
        public string? ModelSet { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool Pending { get; set; }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
    [JsonSerializable(typeof(Wire))]
    internal partial class FileJsonContext : JsonSerializerContext;
}
