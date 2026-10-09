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

    /// <summary>The body with its keys renamed, or the body unchanged when it is not a JSON object.</summary>
    public static string Rename(string body) {
        JsonNode? root;

        try {
            root = JsonNode.Parse(body);
        } catch (JsonException) {
            return body;
        }

        if (root is not JsonObject obj) return body;

        RenameKeys(obj, Roots);

        foreach (var name in Roots.Values) {
            switch (obj[name]) {
                case JsonObject page:
                    RenameKeys(page, Ids);

                    break;
                case JsonArray pages:
                    foreach (var item in pages)
                        if (item is JsonObject listed) RenameKeys(listed, Ids);

                    break;
            }
        }

        return obj.ToJsonString(Output);
    }

    /// <summary>A key whose new name is already taken keeps its old one, so nothing is overwritten.</summary>
    static void RenameKeys(JsonObject obj, Dictionary<string, string> renames) {
        var properties = obj.ToList();
        obj.Clear();

        foreach (var (key, value) in properties) {
            var target = renames.GetValueOrDefault(key, key);
            obj[target != key && properties.Exists(p => p.Key == target) ? key : target] = value;
        }
    }
}
