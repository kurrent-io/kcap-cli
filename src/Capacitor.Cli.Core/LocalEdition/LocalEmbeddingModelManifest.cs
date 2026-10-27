using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Install;

namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>The built-in embedding model set a signed bundle manifest names: the same shape as the server's
/// <c>embedding-model.json</c>. The files' <c>source_url</c> is the publisher's concern and is ignored.</summary>
public sealed record LocalEmbeddingModelManifest(string ModelId, int Contract, string ModelSet, IReadOnlyList<LocalEmbeddingModelFile> Files) {
    public long TotalSize => Files.Sum(f => f.Size);

    /// <summary>The manifest, or null when the node is not a well-formed entry.</summary>
    public static LocalEmbeddingModelManifest? Parse(JsonNode? node) {
        if (node is not JsonObject o) return null;

        var modelId  = Str(o, "model_id");
        var contract = o["contract"] is JsonValue c && c.TryGetValue<int>(out var n) ? n : 0;
        var set      = Str(o, "model_set");

        if (string.IsNullOrWhiteSpace(modelId) || contract <= 0 || !ReleaseManifest.IsValidSha256(set)) return null;
        if (o["files"] is not JsonArray { Count: > 0 } array) return null;

        var files = new List<LocalEmbeddingModelFile>();

        foreach (var item in array) {
            if (item is not JsonObject f) return null;

            var name = Str(f, "name");
            var sha  = Str(f, "sha256");
            var size = f["size"] is JsonValue s && s.TryGetValue<long>(out var l) ? l : 0;

            if (!LocalEmbeddingModelFile.IsSafeName(name) || !ReleaseManifest.IsValidSha256(sha) || size <= 0) return null;
            if (files.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) return null;

            files.Add(new(name!, sha!.ToLowerInvariant(), size));
        }

        return new(modelId, contract, set!, files);
    }

    static string? Str(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
