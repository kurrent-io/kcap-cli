using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core.Harness.Muse;

/// <summary>The XDG roots Muse was installed under, kept for its hooks: Muse runs a hook in a cleared
/// environment with no <c>XDG_*</c> and no <c>KCAP_CONFIG_DIR</c>, so the hook resolves kcap's config
/// root from the home alone and the file lives there, whatever root the installing shell used.</summary>
public static class MuseHookRoots {
    const string FileName      = "muse-roots.json";
    const string ConfigHomeKey = "xdg_config_home";
    const string DataHomeKey   = "xdg_data_home";

    public static string PathFor(UserHome home) => ConfigRoot.UnderHome(home.Path).Path(FileName);

    /// <summary>The persisted <c>XDG_CONFIG_HOME</c> and <c>XDG_DATA_HOME</c>, each null when it is the
    /// default or nothing readable is persisted.</summary>
    public static (string? ConfigHome, string? DataHome) Read(UserHome home) {
        var path = PathFor(home);

        try {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) return default;

            return (StringOf(root, ConfigHomeKey), StringOf(root, DataHomeKey));
        } catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) {
            return default;
        }
    }

    /// <summary>Records each of <paramref name="paths"/>' roots that differs from the default, or clears
    /// the file when neither does. False when it could not be written.</summary>
    public static bool Persist(UserHome home, MusePaths paths) {
        var defaults = new MusePaths(home, null, null);
        var root     = new JsonObject();

        if (!SamePath(paths.ConfigRoot, defaults.ConfigRoot)) root[ConfigHomeKey] = Path.GetDirectoryName(paths.ConfigRoot);
        if (!SamePath(paths.DataRoot, defaults.DataRoot)) root[DataHomeKey] = Path.GetDirectoryName(paths.DataRoot);

        if (root.Count == 0) return Clear(home);

        try {
            var path = PathFor(home);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.Replace(path, root.ToJsonString());
            return true;
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    public static bool Clear(UserHome home) {
        try {
            var path = PathFor(home);
            if (File.Exists(path)) File.Delete(path);
            return true;
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.Ordinal);

    static string? StringOf(JsonObject root, string key) =>
        root[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
}
