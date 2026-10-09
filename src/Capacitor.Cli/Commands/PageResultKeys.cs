using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Commands;

/// <summary>
/// The server's artefact API names its fields <c>artefact</c>, <c>artefacts</c> and <c>artefact_id</c>;
/// the page tools take <c>page_id</c>. Renaming those keys in a tool result lets an agent pass back the
/// id it was given under the name the tool asks for. Only the server's envelope is renamed — the root
/// keys and the id on the page objects under them — because an answer's <c>payload</c> comes back as
/// submitted and may carry any key at all. Values are never touched.
/// </summary>
static class PageResultKeys {
    static readonly JsonSerializerOptions Output = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static readonly Dictionary<string, string> Roots = new(StringComparer.Ordinal) {
        ["artefact"]  = "page",
        ["artefacts"] = "pages",
    };

    static readonly Dictionary<string, string> Ids = new(StringComparer.Ordinal) { ["artefact_id"] = "page_id" };

    /// <summary>The body with its keys renamed, or the body byte-for-byte when there is nothing to rename.</summary>
    public static string Rename(string body) {
        JsonNode? root;

        try {
            root = JsonNode.Parse(body);
        } catch (JsonException) {
            return body;
        }

        if (root is not JsonObject obj) return body;

        var renamed = RenameKeys(obj, Roots);

        foreach (var name in Roots.Values) {
            switch (obj[name]) {
                case JsonObject page:
                    renamed |= RenameKeys(page, Ids);

                    break;
                case JsonArray pages:
                    foreach (var item in pages)
                        if (item is JsonObject listed) renamed |= RenameKeys(listed, Ids);

                    break;
            }
        }

        return renamed ? obj.ToJsonString(Output) : body;
    }

    /// <summary>A key whose new name is already taken keeps its old one, so nothing is overwritten.</summary>
    static bool RenameKeys(JsonObject obj, Dictionary<string, string> renames) {
        var properties = obj.ToList();
        var targets = properties.Select(p => renames.TryGetValue(p.Key, out var target) && !properties.Exists(q => q.Key == target)
            ? target
            : p.Key).ToList();

        if (targets.SequenceEqual(properties.Select(p => p.Key))) return false;

        obj.Clear();
        for (var i = 0; i < properties.Count; i++) obj[targets[i]] = properties[i].Value;

        return true;
    }
}
