using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Antigravity;

/// <summary>
/// Installs / removes the kcap Antigravity capture <b>plugin</b>. The plugin dir
/// (<see cref="AntigravityPaths.PluginDir"/>) holds two files written here: a required
/// <c>plugin.json</c> manifest (<see cref="PluginManifestFileName"/>) — without which the
/// GUI never loads the dir — and a <c>hooks.json</c> registering the kcap control hooks.
/// The <c>hooks.json</c> merge preserves any user-authored blocks; only kcap's block
/// (<see cref="AntigravityHooks.BlockName"/>) is added/replaced/removed. Malformed existing
/// JSON is backed up to <c>hooks.json.bak</c> and replaced (never silently clobbered). A
/// sibling marker (<see cref="MarkerFileName"/>) records the installed version. Mirrors the
/// <see cref="Gemini.GeminiHooksInstaller"/> marker discipline.
/// </summary>
public static class AntigravityHooksInstaller {
    public const string MarkerFileName = ".kcap-hooks-version";
    public const string PluginManifestFileName = "plugin.json";

    static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static void Install(string hooksPath) {
        var root = LoadOrBackup(hooksPath);
        root[AntigravityHooks.BlockName] = AntigravityHooks.BuildKcapBlock();
        Write(hooksPath, root);
        WritePluginManifest(hooksPath);
        WriteMarker(hooksPath);
    }

    public static void Remove(string hooksPath) {
        if (File.Exists(hooksPath)) {
            JsonObject? root = null;
            try { root = JsonNode.Parse(File.ReadAllText(hooksPath)) as JsonObject; } catch { /* malformed → leave file, just drop marker */ }

            if (root is not null && root.Remove(AntigravityHooks.BlockName)) {
                // hooks.json lives in the kcap-owned plugin dir. If nothing but the kcap block
                // was there, drop the now-empty file rather than leaving an orphan {} behind.
                if (root.Count == 0) TryDelete(hooksPath);
                else Write(hooksPath, root);
            }
        }
        DeletePluginManifest(hooksPath);
        DeleteMarker(hooksPath);
    }

    /// <summary>Marker present, or the kcap block is detectable in the file.</summary>
    public static bool IsInstalled(string hooksPath) {
        var dir = Path.GetDirectoryName(hooksPath);
        if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, MarkerFileName))) return true;
        if (!File.Exists(hooksPath)) return false;

        try {
            if (JsonNode.Parse(File.ReadAllText(hooksPath)) is JsonObject root)
                return AntigravityHooks.BlockReferencesKcap(root[AntigravityHooks.BlockName]);
        } catch { /* malformed → not installed */ }
        return false;
    }

    public static string? ReadMarker(string hooksPath) {
        var dir = Path.GetDirectoryName(hooksPath);
        if (string.IsNullOrEmpty(dir)) return null;
        var marker = Path.Combine(dir, MarkerFileName);
        try { return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null; }
        catch { return null; }
    }

    static JsonObject LoadOrBackup(string hooksPath) {
        if (!File.Exists(hooksPath)) return new JsonObject();

        try {
            if (JsonNode.Parse(File.ReadAllText(hooksPath)) is JsonObject root) return root;
        } catch { /* fall through to backup */ }

        // Malformed or non-object → back up so a user's file is never silently clobbered.
        try { File.Copy(hooksPath, hooksPath + ".bak", overwrite: true); } catch { /* best effort */ }
        return new JsonObject();
    }

    static void Write(string hooksPath, JsonObject root) {
        var dir = Path.GetDirectoryName(hooksPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(hooksPath, root.ToJsonString(WriteOptions));
    }

    static void WritePluginManifest(string hooksPath) {
        var dir = Path.GetDirectoryName(hooksPath);
        if (string.IsNullOrEmpty(dir)) return;
        try {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, PluginManifestFileName),
                AntigravityHooks.BuildPluginManifest().ToJsonString(WriteOptions));
        } catch { /* best effort */ }
    }

    static void TryDelete(string path) {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    static void DeletePluginManifest(string hooksPath) {
        var dir = Path.GetDirectoryName(hooksPath);
        if (string.IsNullOrEmpty(dir)) return;
        var manifest = Path.Combine(dir, PluginManifestFileName);
        try { if (File.Exists(manifest)) File.Delete(manifest); } catch { /* best effort */ }
    }

    static void WriteMarker(string hooksPath) {
        var dir = Path.GetDirectoryName(hooksPath);
        if (string.IsNullOrEmpty(dir)) return;
        try {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, MarkerFileName), CapacitorVersion.Current());
        } catch { /* best effort */ }
    }

    static void DeleteMarker(string hooksPath) {
        var dir = Path.GetDirectoryName(hooksPath);
        if (string.IsNullOrEmpty(dir)) return;
        var marker = Path.Combine(dir, MarkerFileName);
        try { if (File.Exists(marker)) File.Delete(marker); } catch { /* best effort */ }
    }
}
