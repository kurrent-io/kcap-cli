using System.Text.Json;
using System.Text.Json.Nodes;

namespace kapacitor.Commands;

public static class PluginCommand {
    static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    static readonly string[] CodexHookEvents = [
        "SessionStart",
        "UserPromptSubmit",
        "PreToolUse",
        "PostToolUse",
        "PermissionRequest",
        "Stop"
    ];

    const string CodexHookCommand = "kapacitor codex-hook";

    public static async Task<int> HandleAsync(string[] args) {
        if (args.Length < 2) {
            PrintUsage();

            return 1;
        }

        return args[1] switch {
            "install" => await Install(args),
            "remove"  => await Remove(args),
            _         => PrintUsage()
        };
    }

    static async Task<int> Install(string[] args) {
        if (args.Contains("--codex")) {
            return await InstallCodex(args);
        }

        return await InstallClaude(args);
    }

    static async Task<int> Remove(string[] args) {
        if (args.Contains("--codex")) {
            return await RemoveCodex(args);
        }

        return await RemoveClaude(args);
    }

    static async Task<int> InstallClaude(string[] args) {
        var pluginPath = SetupCommand.ResolvePluginPath();

        if (pluginPath is null) {
            await Console.Error.WriteLineAsync("Plugin directory not found. Re-install kapacitor via npm:");
            await Console.Error.WriteLineAsync("  npm install -g @kurrent/kapacitor");

            return 1;
        }

        var scope = args.Contains("--project") ? "project" : "user";

        var settingsPath = scope == "project"
            ? Path.Combine(Environment.CurrentDirectory, ".claude", "settings.local.json")
            : ClaudePaths.UserSettings;

        var installed = SetupCommand.InstallPlugin(settingsPath, pluginPath);

        if (installed) {
            await Console.Out.WriteLineAsync($"Plugin installed ({scope}: {settingsPath})");
        } else {
            await Console.Error.WriteLineAsync("Could not update settings file.");

            return 1;
        }

        return 0;
    }

    static async Task<int> RemoveClaude(string[] args) {
        var scope = args.Contains("--project") ? "project" : "user";

        var settingsPath = scope == "project"
            ? Path.Combine(Environment.CurrentDirectory, ".claude", "settings.local.json")
            : ClaudePaths.UserSettings;

        if (!File.Exists(settingsPath)) {
            await Console.Out.WriteLineAsync("Nothing to remove — settings file not found.");

            return 0;
        }

        try {
            var text = await File.ReadAllTextAsync(settingsPath);

            if (JsonNode.Parse(text) is not JsonObject root) {
                await Console.Out.WriteLineAsync("Nothing to remove.");

                return 0;
            }

            var changed = false;

            if (root["enabledPlugins"] is JsonObject enabled) {
                changed |= enabled.Remove("kapacitor@kapacitor");
                changed |= enabled.Remove("kapacitor@kurrent");
            }

            if (root["extraKnownMarketplaces"] is JsonObject marketplaces) {
                changed |= marketplaces.Remove("kapacitor");
                changed |= marketplaces.Remove("kurrent");
            }

            if (changed) {
                await File.WriteAllTextAsync(settingsPath, root.ToJsonString(WriteOpts));
                await Console.Out.WriteLineAsync($"Plugin removed ({scope}: {settingsPath})");
            } else {
                await Console.Out.WriteLineAsync("Plugin was not installed.");
            }

            return 0;
        } catch (Exception ex) {
            await Console.Error.WriteLineAsync($"Could not update settings: {ex.Message}");

            return 1;
        }
    }

    static async Task<int> InstallCodex(string[] args) {
        var scope = args.Contains("--project") ? "project" : "user";

        var hooksPath = scope == "project"
            ? Path.Combine(Environment.CurrentDirectory, ".codex", "hooks.json")
            : CodexPaths.UserHooksJson;

        if (!InstallCodexHooks(hooksPath)) {
            await Console.Error.WriteLineAsync("Could not write Codex hooks file.");

            return 1;
        }

        await Console.Out.WriteLineAsync($"Codex hooks installed ({scope}: {hooksPath})");

        if (scope == "project") {
            await Console.Out.WriteLineAsync(
                "Note: Codex requires the project's .codex directory to be trusted. " +
                "Run `codex` once in this directory and accept the trust prompt."
            );
        }

        return 0;
    }

    static async Task<int> RemoveCodex(string[] args) {
        var scope = args.Contains("--project") ? "project" : "user";

        var hooksPath = scope == "project"
            ? Path.Combine(Environment.CurrentDirectory, ".codex", "hooks.json")
            : CodexPaths.UserHooksJson;

        if (!File.Exists(hooksPath)) {
            await Console.Out.WriteLineAsync("Nothing to remove — hooks file not found.");

            return 0;
        }

        if (RemoveCodexHooks(hooksPath)) {
            await Console.Out.WriteLineAsync($"Codex hooks removed ({scope}: {hooksPath})");
        } else {
            await Console.Out.WriteLineAsync("Codex hooks were not installed.");
        }

        return 0;
    }

    /// <summary>
    /// Writes (or merges into) <paramref name="hooksPath"/> a hooks.json that
    /// invokes <c>kapacitor codex-hook</c> for every Codex event. Existing
    /// non-kapacitor entries are preserved; existing kapacitor entries are
    /// replaced (so the timeout/command stay current after a CLI upgrade).
    /// </summary>
    public static bool InstallCodexHooks(string hooksPath) {
        try {
            JsonObject root = [];

            if (File.Exists(hooksPath)) {
                try {
                    if (JsonNode.Parse(File.ReadAllText(hooksPath)) is JsonObject obj) root = obj;
                } catch {
                    // Malformed — start fresh
                }
            }

            if (root["hooks"] is not JsonObject hooks) {
                hooks         = [];
                root["hooks"] = hooks;
            }

            foreach (var evt in CodexHookEvents) {
                var kapacitorEntry = new JsonObject {
                    ["hooks"] = new JsonArray(
                        new JsonObject {
                            ["type"]    = "command",
                            ["command"] = CodexHookCommand,
                            ["timeout"] = 30
                        }
                    )
                };

                if (hooks[evt] is not JsonArray entries) {
                    hooks[evt] = new JsonArray(kapacitorEntry);
                    continue;
                }

                var preserved = new JsonArray();

                foreach (var entry in entries) {
                    if (entry is null) continue;

                    if (!EntryReferencesKapacitorCodexHook(entry)) {
                        preserved.Add(entry.DeepClone());
                    }
                }

                preserved.Add((JsonNode)kapacitorEntry);
                hooks[evt] = preserved;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(hooksPath)!);
            File.WriteAllText(hooksPath, root.ToJsonString(WriteOpts));

            return true;
        } catch {
            return false;
        }
    }

    /// <summary>
    /// Removes every entry in <paramref name="hooksPath"/> whose command
    /// invokes <c>kapacitor codex-hook</c>. Other entries are preserved.
    /// Returns true if any entries were removed.
    /// </summary>
    public static bool RemoveCodexHooks(string hooksPath) {
        try {
            if (!File.Exists(hooksPath)) return false;

            if (JsonNode.Parse(File.ReadAllText(hooksPath)) is not JsonObject root) return false;
            if (root["hooks"] is not JsonObject hooks) return false;

            var changed = false;

            foreach (var evt in CodexHookEvents) {
                if (hooks[evt] is not JsonArray entries) continue;

                var preserved = new JsonArray();

                foreach (var entry in entries) {
                    if (entry is null) continue;

                    if (EntryReferencesKapacitorCodexHook(entry)) {
                        changed = true;
                    } else {
                        preserved.Add(entry.DeepClone());
                    }
                }

                hooks[evt] = preserved;
            }

            if (changed) {
                File.WriteAllText(hooksPath, root.ToJsonString(WriteOpts));
            }

            return changed;
        } catch {
            return false;
        }
    }

    /// <summary>
    /// Returns true iff <paramref name="entry"/> is a JsonObject that has a
    /// <c>hooks</c> JsonArray containing at least one JsonObject whose
    /// <c>command</c> string contains <c>kapacitor codex-hook</c>.
    /// Any non-conformant node shape is treated as a non-match (returns false)
    /// instead of throwing.
    /// </summary>
    internal static bool EntryReferencesKapacitorCodexHook(JsonNode? entry) {
        if (entry is not JsonObject entryObj) return false;
        if (entryObj["hooks"] is not JsonArray inner) return false;

        return inner.OfType<JsonObject>().Any(h =>
            h["command"] is JsonValue v
            && v.TryGetValue<string>(out var cmd)
            && cmd.Contains("kapacitor codex-hook"));
    }

    static int PrintUsage() {
        Console.Error.WriteLine("Usage: kapacitor plugin <install|remove> [--project] [--codex]");

        return 1;
    }
}
