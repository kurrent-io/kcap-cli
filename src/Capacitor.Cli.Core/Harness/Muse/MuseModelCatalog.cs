using System.Text.Json;

namespace Capacitor.Cli.Core.Harness.Muse;

/// <summary>Reads the served-model catalog Muse caches under <see cref="MusePaths.ModelCatalogDir"/>:
/// one <c>schema_version</c> 2 document per provider profile, whose <c>rows</c> are the models the
/// provider currently serves. Only the launch provider's <c>visible</c> rows are launchable from a picker.</summary>
public static class MuseModelCatalog {
    /// <summary>The provider every hosted launch names; a catalog cached for another one lists models
    /// that launch cannot reach.</summary>
    public const string LaunchProvider = "meta";

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

    /// <summary>Appends the visible rows of one document. A document of another schema, another
    /// provider, or one that is not a catalog at all contributes nothing.</summary>
    internal static void Parse(string json, List<Row> rows) {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return; }

        using (doc) {
            var root = doc.RootElement;
            if (root.Num("schema_version") != SupportedSchemaVersion) return;
            if (root.Str("provider_id") != LaunchProvider) return;
            if (root.Arr("rows") is not { } list) return;

            foreach (var row in list.EnumerateArray()) {
                if (row.Str("model_id") is not { Length: > 0 } id) continue;
                if (row.Str("visibility") != "visible") continue;

                var order = row.Num("display_order") is { } n && n is >= int.MinValue and <= int.MaxValue ? (int?)n : null;
                rows.Add(new Row(id, row.Str("display_label") is { Length: > 0 } label ? label : id, row.Str("release_date"), order));
            }
        }
    }

    internal sealed record Row(string ModelId, string Label, string? ReleaseDate, int? DisplayOrder);
}
