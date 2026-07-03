using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Antigravity;

/// <summary>
/// Installs / removes kcap's hook block in an Antigravity <c>hooks.json</c> — global
/// (<see cref="AntigravityPaths.GlobalHooksJson"/>) or per-workspace
/// (<see cref="AntigravityPaths.WorkspaceHooksJson"/>). The merge preserves any
/// user-authored blocks; only kcap's block (<see cref="AntigravityHooks.BlockName"/>)
/// is added/replaced/removed. Malformed existing JSON is backed up to
/// <c>hooks.json.bak</c> and replaced (never silently clobbered). A sibling marker
/// (<see cref="MarkerFileName"/>) records the installed version. Mirrors the
/// <see cref="Gemini.GeminiHooksInstaller"/> marker discipline.
/// </summary>
public static class AntigravityHooksInstaller {
    public const string MarkerFileName = ".kcap-hooks-version";

    static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static void Install(string hooksPath) {
        var root = LoadOrBackup(hooksPath);
        root[AntigravityHooks.BlockName] = AntigravityHooks.BuildKcapBlock();
        Write(hooksPath, root);
        WriteMarker(hooksPath);
    }

    public static void Remove(string hooksPath) {
        if (File.Exists(hooksPath)) {
            JsonObject? root = null;
            try { root = JsonNode.Parse(File.ReadAllText(hooksPath)) as JsonObject; } catch { /* malformed → leave file, just drop marker */ }

            if (root is not null && root.Remove(AntigravityHooks.BlockName))
                Write(hooksPath, root);
        }
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
