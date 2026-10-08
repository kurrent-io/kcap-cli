using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core.Harness;

/// <summary>Edits a vendor's JSON settings file in place of the user's own: an unparseable file is
/// refused rather than replaced, because resetting it would discard every setting the user has.</summary>
public static class JsonSettingsFile {
    static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    public static SettingsEdit Edit(string path, Func<JsonObject, bool> edit, bool createIfMissing = true) {
        try {
            JsonObject root;

            if (File.Exists(path)) {
                var text = File.ReadAllText(path);
                JsonNode? parsed;
                try { parsed = string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text); }
                catch (JsonException) { return SettingsEdit.Malformed; }
                if (parsed is not JsonObject obj) return SettingsEdit.Malformed;
                root = obj;
            } else {
                if (!createIfMissing) return SettingsEdit.Unchanged;
                root = [];
            }

            if (!edit(root)) return SettingsEdit.Unchanged;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.Replace(path, root.ToJsonString(WriteOpts));

            return SettingsEdit.Changed;
        } catch (IOException) {
            return SettingsEdit.Failed;
        } catch (UnauthorizedAccessException) {
            return SettingsEdit.Failed;
        }
    }
}
