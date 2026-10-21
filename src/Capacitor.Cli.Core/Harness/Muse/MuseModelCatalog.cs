using System.Text.Json;

namespace Capacitor.Cli.Core.Harness.Muse;

/// <summary>Reads the served-model catalog Muse caches under <see cref="MusePaths.ModelCatalogDir"/>:
/// one <c>schema_version</c> 2 document per provider profile, whose <c>rows</c> are the models the
/// provider currently serves. Only <c>visible</c> rows are launchable from a picker.</summary>
public static class MuseModelCatalog {
    const int SupportedSchemaVersion = 2;

    /// <summary>The catalog files, sorted by name so every reader sees the same order.</summary>
    public static IReadOnlyList<string> Files(string dir) {
        try {
            if (!Directory.Exists(dir)) return [];
            return Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal).ToArray();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return [];
        }
    }

    /// <summary>Null when no file yields a visible model, never an empty list: an empty list would
    /// suppress the server's catalog while saying nothing about what Muse can launch. An id listed by
    /// several profiles takes its row from the first file by name.</summary>
    public static IReadOnlyList<VendorModelOption>? Read(string dir) {
        var rows = new List<Row>();
        foreach (var file in Files(dir)) {
            string json;
            try { json = File.ReadAllText(file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            Parse(json, rows);
        }

        if (rows.Count == 0) return null;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        return rows
            .Where(r => seen.Add(r.ModelId))
            .OrderBy(r => r.DisplayOrder ?? int.MaxValue)
            .ThenByDescending(r => r.ReleaseDate ?? "", StringComparer.Ordinal)
            .ThenBy(r => r.ModelId, StringComparer.Ordinal)
            .Select(r => new VendorModelOption(r.ModelId, r.Label))
            .ToArray();
    }

    /// <summary>Appends the visible rows of one document; a document of another schema, or one that is
    /// not a catalog at all, contributes nothing.</summary>
    internal static void Parse(string json, List<Row> rows) {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return; }

        using (doc) {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (!root.TryGetProperty("schema_version", out var schema)
             || schema.ValueKind != JsonValueKind.Number
             || !schema.TryGetInt32(out var version)
             || version != SupportedSchemaVersion) return;
            if (!root.TryGetProperty("rows", out var list) || list.ValueKind != JsonValueKind.Array) return;

            foreach (var row in list.EnumerateArray()) {
                if (row.ValueKind != JsonValueKind.Object) continue;
                if (Str(row, "model_id") is not { Length: > 0 } id) continue;
                if (Str(row, "visibility") != "visible") continue;

                int? order = row.TryGetProperty("display_order", out var o) && o.ValueKind == JsonValueKind.Number && o.TryGetInt32(out var n)
                    ? n
                    : null;
                rows.Add(new Row(id, Str(row, "display_label") is { Length: > 0 } label ? label : id, Str(row, "release_date"), order));
            }
        }
    }

    static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal sealed record Row(string ModelId, string Label, string? ReleaseDate, int? DisplayOrder);
}
