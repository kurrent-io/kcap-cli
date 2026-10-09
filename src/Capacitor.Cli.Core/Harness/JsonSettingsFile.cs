using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core.Harness;

/// <summary>Edits a vendor's JSON settings file in place of the user's own: an unparseable file is
/// refused rather than replaced, because resetting it would discard every setting the user has.</summary>
public static class JsonSettingsFile {
    static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    /// <summary>Holds the file's <see cref="ConfigFileLock"/> from the read to the replace, so two kcap
    /// processes cannot each drop the other's edit. A caller holding the account registry lock takes
    /// it first; nothing here may take the registry lock.</summary>
    public static SettingsEdit Edit(string path, Func<JsonObject, bool> edit, bool createIfMissing = true, TimeSpan? lockTimeout = null) {
        try {
            using var _ = ConfigFileLock.Acquire(PhysicalPath.Of(path), lockTimeout);
            JsonObject root;

            if (File.Exists(path)) {
                var text = File.ReadAllTextShared(path);
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
        } catch (TimeoutException) {
            return SettingsEdit.Failed;
        } catch (WaitHandleCannotBeOpenedException) {
            return SettingsEdit.Failed;
        }
    }
}
