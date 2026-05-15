using System.Text.Json;
using System.Text.Json.Nodes;

namespace kapacitor.Commands;

public static class PluginCommand {
    static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    // Folders inside <plugin>/codex-skills/ that we ship to Codex. Each name is
    // also the target folder under <codex>/skills/, so on remove we can delete
    // them without re-reading the source tree. Add a new skill here when adding
    // it to codex-skills/.
    static readonly string[] CodexSkillNames = [
        "kapacitor-recap",
        "kapacitor-errors"
    ];

    const string CodexHookCommand = "kapacitor codex-hook";

    // PermissionRequest must wait for the dashboard's decision; the daemon-side
    // bridge call is intentionally infinite. 86400s = 24h keeps Codex from
    // killing the hook before the user approves or denies.
    const int PermissionRequestTimeout = 86400;
    const int DefaultHookTimeout       = 30;

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
        await Console.Out.WriteLineAsync(
            "Next: run /hooks inside Codex and trust each kapacitor entry — " +
            "Codex won't execute hooks until each is explicitly trusted."
        );

        // Skills are user-scoped only. Codex auto-discovers from
        // ~/.codex/skills (or $CODEX_HOME/skills), and project-level skill
        // discovery isn't documented — so we keep behaviour consistent and
        // predictable by always installing them user-wide.
        var pluginPath = SetupCommand.ResolvePluginPath();
        var skillsSource = pluginPath is null ? null : Path.Combine(pluginPath, "codex-skills");

        if (skillsSource is not null && Directory.Exists(skillsSource)) {
            if (InstallCodexSkills(skillsSource, CodexPaths.UserSkillsDir)) {
                await Console.Out.WriteLineAsync($"Codex skills installed (user: {CodexPaths.UserSkillsDir})");
            } else {
                // Hooks succeeded but skills failed — `--codex` is a hooks AND
                // skills contract, so surface the partial failure via exit code
                // so callers (CI, scripts) can detect it.
                await Console.Error.WriteLineAsync("Could not install Codex skills.");
                return 1;
            }
        }

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

        var hooksRemoved = File.Exists(hooksPath) && RemoveCodexHooks(hooksPath);

        if (hooksRemoved) {
            await Console.Out.WriteLineAsync($"Codex hooks removed ({scope}: {hooksPath})");
        }

        // Skills always installed user-wide — remove them regardless of scope.
        var skillsRemoved = RemoveCodexSkills(CodexPaths.UserSkillsDir);

        if (skillsRemoved) {
            await Console.Out.WriteLineAsync($"Codex skills removed (user: {CodexPaths.UserSkillsDir})");
        }

        if (!hooksRemoved && !skillsRemoved) {
            await Console.Out.WriteLineAsync("Nothing to remove — hooks and skills were not installed.");
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

            foreach (var evt in CodexHooksParser.CodexHookEvents) {
                var timeout        = evt == "PermissionRequest" ? PermissionRequestTimeout : DefaultHookTimeout;
                var kapacitorEntry = new JsonObject {
                    ["hooks"] = new JsonArray(
                        new JsonObject {
                            ["type"]    = "command",
                            ["command"] = CodexHookCommand,
                            ["timeout"] = timeout
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

                    if (!CodexHooksParser.EntryReferencesKapacitorCodexHook(entry)) {
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

            foreach (var evt in CodexHooksParser.CodexHookEvents) {
                if (hooks[evt] is not JsonArray entries) continue;

                var preserved = new JsonArray();

                foreach (var entry in entries) {
                    if (entry is null) continue;

                    if (CodexHooksParser.EntryReferencesKapacitorCodexHook(entry)) {
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
    /// Copies every skill folder in <paramref name="sourceDir"/> (each
    /// containing a <c>SKILL.md</c>) into <paramref name="targetDir"/>, one
    /// subdirectory per skill. Existing kapacitor-owned folders (by name) are
    /// replaced wholesale so the bundled SKILL.md stays current after a CLI
    /// upgrade. Foreign folders in <paramref name="targetDir"/> are untouched.
    /// </summary>
    public static bool InstallCodexSkills(string sourceDir, string targetDir) {
        if (!Directory.Exists(sourceDir)) return false;

        try {
            Directory.CreateDirectory(targetDir);

            foreach (var name in CodexSkillNames) {
                var src = Path.Combine(sourceDir, name);

                if (!Directory.Exists(src)) continue;

                var dst = Path.Combine(targetDir, name);

                if (Directory.Exists(dst)) Directory.Delete(dst, recursive: true);

                CopyDirectory(src, dst);
            }

            return true;
        } catch {
            return false;
        }
    }

    /// <summary>
    /// Deletes every kapacitor-owned skill folder from <paramref name="targetDir"/>.
    /// Returns true if any folder was actually removed. Foreign folders are
    /// left intact.
    /// </summary>
    public static bool RemoveCodexSkills(string targetDir) {
        if (!Directory.Exists(targetDir)) return false;

        var changed = false;

        foreach (var name in CodexSkillNames) {
            var dst = Path.Combine(targetDir, name);

            if (!Directory.Exists(dst)) continue;

            try {
                Directory.Delete(dst, recursive: true);
                changed = true;
            } catch (Exception ex) {
                // Log so the user knows a skill is stuck. Exit code stays 0 for
                // parity with RemoveCodexHooks, which also degrades gracefully.
                Console.Error.WriteLine($"Could not remove Codex skill '{name}': {ex.Message}");
            }
        }

        return changed;
    }

    static void CopyDirectory(string source, string destination) {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source)) {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(source)) {
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }


    static int PrintUsage() {
        Console.Error.WriteLine("Usage: kapacitor plugin <install|remove> [--project] [--codex]");

        return 1;
    }
}
