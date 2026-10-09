using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Harness.Claude;

public static class ClaudePluginWriter {
    static readonly string[] LegacyMarketplaces = ["kurrent", "kapacitor"];
    static readonly string[] LegacyPlugins      = ["kcap@kurrent", "kapacitor@kapacitor", "kapacitor@kurrent"];

    public static SettingsEdit Install(string settingsPath, string marketplacePath) {
        var result = JsonSettingsFile.Edit(settingsPath, root => {
            var marketplaces = root["extraKnownMarketplaces"] as JsonObject ?? [];
            root["extraKnownMarketplaces"] = marketplaces;
            var entry = new JsonObject {
                ["source"] = new JsonObject { ["source"] = "directory", ["path"] = marketplacePath }
            };
            var changed = marketplaces["kcap"]?.ToJsonString() != entry.ToJsonString();
            marketplaces["kcap"] = entry;
            foreach (var name in LegacyMarketplaces) changed |= marketplaces.Remove(name);

            var enabled = root["enabledPlugins"] as JsonObject ?? [];
            root["enabledPlugins"] = enabled;
            changed |= enabled["kcap@kcap"]?.ToJsonString() != "true";
            enabled["kcap@kcap"] = true;
            foreach (var name in LegacyPlugins) changed |= enabled.Remove(name);

            return changed;
        });

        if (result is SettingsEdit.Changed or SettingsEdit.Unchanged) ClaudePluginInstaller.WriteMarker(settingsPath);

        return result;
    }

    public static SettingsEdit Remove(string settingsPath) {
        var result = JsonSettingsFile.Edit(settingsPath, root => {
            var changed = false;
            if (root["enabledPlugins"] is JsonObject enabled) {
                changed |= enabled.Remove("kcap@kcap");
                foreach (var name in LegacyPlugins) changed |= enabled.Remove(name);
            }
            if (root["extraKnownMarketplaces"] is JsonObject marketplaces) {
                changed |= marketplaces.Remove("kcap");
                foreach (var name in LegacyMarketplaces) changed |= marketplaces.Remove(name);
            }
            return changed;
        }, createIfMissing: false);

        if (result is SettingsEdit.Changed) ClaudePluginInstaller.DeleteMarker(settingsPath);

        return result;
    }
}
