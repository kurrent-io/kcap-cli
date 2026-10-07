using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Commands;

/// <summary>
/// The server's artefact API names its fields <c>artefact</c>, <c>artefacts</c> and <c>artefact_id</c>;
/// the page tools take <c>page_id</c>. Renaming those keys in a tool result lets an agent pass back the
/// id it was given under the name the tool asks for. Values are never touched.
/// </summary>
static class PageResultKeys {
    static readonly JsonSerializerOptions Output = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static readonly Dictionary<string, string> Renames = new(StringComparer.Ordinal) {
        ["artefact"]    = "page",
        ["artefacts"]   = "pages",
        ["artefact_id"] = "page_id",
    };

    /// <summary>The body with its keys renamed, or the body unchanged when it is not JSON.</summary>
    public static string Rename(string body) {
        JsonNode? root;

        try {
            root = JsonNode.Parse(body);
        } catch (JsonException) {
            return body;
        }

        if (root is null) return body;

        Walk(root);

        return root.ToJsonString(Output);
    }

    static void Walk(JsonNode node) {
        switch (node) {
            case JsonObject obj: {
                var properties = obj.ToList();
                obj.Clear();

                foreach (var (key, value) in properties) {
                    if (value is not null) Walk(value);
                    obj[Renames.GetValueOrDefault(key, key)] = value;
                }

                break;
            }
            case JsonArray array:
                foreach (var item in array)
                    if (item is not null) Walk(item);

                break;
        }
    }
}
