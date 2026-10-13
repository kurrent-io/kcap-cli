using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Harness.Muse;

/// <summary>Merges kcap's hook groups into Muse's user <c>settings.json</c>. A file Muse cannot
/// parse stops every <c>muse</c> command, so an unparseable one is reported, never rewritten.</summary>
public static class MuseHooksInstaller {
    public const string MarkerFileName = ".kcap-hooks-version";

    public static bool IsInstalled(string settingsPath) {
        var dir = Path.GetDirectoryName(settingsPath);
        if (!string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, MarkerFileName))) return true;
        if (!File.Exists(settingsPath)) return false;
        try {
            return JsonNode.Parse(File.ReadAllTextShared(settingsPath)) is JsonObject root
                && MuseHooksParser.InstalledEvents(root).Count > 0;
        } catch (JsonException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public static SettingsEdit Install(string settingsPath) {
        var outcome = JsonSettingsFile.Edit(settingsPath, root => {
            if (root["hooks"] is not null and not JsonObject) return false;
            var changed = false;
            if (root["schema_version"] is null) { root["schema_version"] = 1; changed = true; }

            var hooks = root["hooks"] as JsonObject;
            if (hooks is null) { hooks = new JsonObject(); root["hooks"] = hooks; changed = true; }

            foreach (var eventName in MuseHooksParser.MuseHookEvents) {
                var groups = hooks[eventName] as JsonArray;
                if (groups is null) { groups = new JsonArray(); hooks[eventName] = groups; changed = true; }
                if (groups.Any(MuseHooksParser.EntryReferencesCapacitorMuseHook)) continue;
                groups.Add((JsonNode?)MuseHooksParser.BuildKcapGroup(eventName));
                changed = true;
            }
            return changed;
        });

        if (outcome is SettingsEdit.Changed or SettingsEdit.Unchanged && IsInstalled(settingsPath)) WriteMarker(settingsPath);
        return outcome;
    }

    public static SettingsEdit Remove(string settingsPath) {
        var outcome = JsonSettingsFile.Edit(settingsPath, root => {
            if (root["hooks"] is not JsonObject hooks) return false;
            var changed = false;
            foreach (var eventName in hooks.Select(kv => kv.Key).ToList()) {
                if (hooks[eventName] is not JsonArray groups) continue;
                var kept = new JsonArray(groups.Where(g => !MuseHooksParser.EntryReferencesCapacitorMuseHook(g)).Select(g => g!.DeepClone()).ToArray());
                if (kept.Count == groups.Count) continue;
                changed = true;
                if (kept.Count == 0) hooks.Remove(eventName); else hooks[eventName] = kept;
            }
            if (changed && hooks.Count == 0) root.Remove("hooks");
            return changed;
        }, createIfMissing: false);

        DeleteMarker(settingsPath);
        return outcome;
    }

    public static string? ReadMarker(string settingsPath) {
        var dir = Path.GetDirectoryName(settingsPath);
        if (string.IsNullOrEmpty(dir)) return null;
        var marker = Path.Combine(dir, MarkerFileName);
        try { return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null; } catch { return null; }
    }

    static void WriteMarker(string settingsPath) {
        var dir = Path.GetDirectoryName(settingsPath);
        if (string.IsNullOrEmpty(dir)) return;
        try { Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, MarkerFileName), CapacitorVersion.Current()); } catch { }
    }

    static void DeleteMarker(string settingsPath) {
        var dir = Path.GetDirectoryName(settingsPath);
        if (string.IsNullOrEmpty(dir)) return;
        var marker = Path.Combine(dir, MarkerFileName);
        try { if (File.Exists(marker)) File.Delete(marker); } catch { }
    }
}
