using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core.Harness.Codex;

public static class CodexHooksWriter {
    public const string HookCommand = "kcap hook --codex";

    // PermissionRequest waits for the dashboard's decision, so Codex must not time it out.
    const int PermissionRequestTimeout = 86400;
    const int DefaultHookTimeout       = 30;

    public static SettingsEdit Install(string hooksPath) {
        var result = JsonSettingsFile.Edit(hooksPath, root => {
            var hooks = root["hooks"] as JsonObject ?? [];
            root["hooks"] = hooks;
            var before = hooks.ToJsonString();

            foreach (var evt in CodexHooksParser.CodexHookEvents) {
                var entry = new JsonObject {
                    ["hooks"] = new JsonArray(new JsonObject {
                        ["type"]    = "command",
                        ["command"] = HookCommand,
                        ["timeout"] = evt == "PermissionRequest" ? PermissionRequestTimeout : DefaultHookTimeout,
                    })
                };

                var kept = new JsonArray();
                if (hooks[evt] is JsonArray existing)
                    foreach (var e in existing)
                        if (e is not null && !CodexHooksParser.EntryReferencesCapacitorCodexHook(e)) kept.Add(e.DeepClone());

                kept.Add((JsonNode)entry);
                hooks[evt] = kept;
            }

            return hooks.ToJsonString() != before;
        });

        if (result is SettingsEdit.Changed or SettingsEdit.Unchanged) CodexHooksInstaller.WriteMarker(hooksPath);

        return result;
    }

    public static SettingsEdit Remove(string hooksPath) {
        var result = JsonSettingsFile.Edit(hooksPath, root => {
            if (root["hooks"] is not JsonObject hooks) return false;
            var changed = false;

            foreach (var evt in CodexHooksParser.CodexHookEvents) {
                if (hooks[evt] is not JsonArray entries) continue;
                var kept = new JsonArray();
                foreach (var e in entries) {
                    if (e is null) continue;
                    if (CodexHooksParser.EntryReferencesCapacitorCodexHook(e)) changed = true;
                    else kept.Add(e.DeepClone());
                }
                hooks[evt] = kept;
            }

            return changed;
        }, createIfMissing: false);

        if (result is SettingsEdit.Changed) CodexHooksInstaller.DeleteMarker(hooksPath);

        return result;
    }
}
