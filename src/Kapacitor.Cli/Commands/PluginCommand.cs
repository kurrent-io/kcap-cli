using System.Text.Json;
using System.Text.Json.Nodes;
using Kapacitor.Cli.Core;

namespace Kapacitor.Cli.Commands;

public static class PluginCommand {
    static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

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
        if (args.Contains("--skills")) {
            return await InstallSkills(args);
        }

        if (args.Contains("--codex")) {
            return await InstallCodex(args);
        }

        return await InstallClaude(args);
    }

    static async Task<int> Remove(string[] args) {
        if (args.Contains("--skills")) {
            return await RemoveSkills(args);
        }

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

    static async Task<int> InstallSkills(string[] _) {
        var pluginPath = SetupCommand.ResolvePluginPath();

        if (pluginPath is null) {
            await Console.Error.WriteLineAsync(
                "Cannot install agent skills: kapacitor plugin folder not found. " +
                "Re-install kapacitor via npm: npm install -g @kurrent/kapacitor");
            return 1;
        }

        var skillsSource = Path.Combine(pluginPath, "skills");

        if (!Directory.Exists(skillsSource)) {
            await Console.Error.WriteLineAsync(
                $"Cannot install agent skills: 'skills' folder missing from {pluginPath}. " +
                "Re-install kapacitor via npm: npm install -g @kurrent/kapacitor");
            return 1;
        }

        if (!AgentsSkillsInstaller.Install(skillsSource, AgentsPaths.UserSkillsDir)) {
            await Console.Error.WriteLineAsync("Could not install agent skills.");
            return 1;
        }

        await Console.Out.WriteLineAsync($"Agent skills installed (user: {AgentsPaths.UserSkillsDir})");

        AgentsSkillsInstaller.CleanLegacyCodexSkills(Path.Combine(CodexPaths.Home, "skills"));
        return 0;
    }

    static async Task<int> RemoveSkills(string[] _) {
        var agentsRemoved = AgentsSkillsInstaller.Remove(AgentsPaths.UserSkillsDir);

        if (agentsRemoved) {
            await Console.Out.WriteLineAsync($"Agent skills removed (user: {AgentsPaths.UserSkillsDir})");
        }

        var legacyRemoved = AgentsSkillsInstaller.CleanLegacyCodexSkills(Path.Combine(CodexPaths.Home, "skills"));

        if (!agentsRemoved && !legacyRemoved) {
            await Console.Out.WriteLineAsync("Nothing to remove — agent skills were not installed.");
        }

        return 0;
    }

    static async Task<int> InstallCodex(string[] args) {
        var scope = args.Contains("--project") ? "project" : "user";

        var hooksPath = scope == "project"
            ? Path.Combine(Environment.CurrentDirectory, ".codex", "hooks.json")
            : CodexPaths.UserHooksJson;

        // `--codex` is an atomic hooks AND skills contract. Resolve the
        // skills source BEFORE writing hooks so a missing plugin folder
        // doesn't leave the user with hooks pointing at a binary whose
        // skills never installed.
        var pluginPath = SetupCommand.ResolvePluginPath();

        if (pluginPath is null) {
            await Console.Error.WriteLineAsync(
                "Cannot install Codex plugin: kapacitor plugin folder not found. " +
                "Re-install kapacitor via npm: npm install -g @kurrent/kapacitor"
            );
            return 1;
        }

        var skillsSource = Path.Combine(pluginPath, "skills");

        if (!Directory.Exists(skillsSource)) {
            await Console.Error.WriteLineAsync(
                $"Cannot install Codex plugin: 'skills' folder missing from {pluginPath}. " +
                "Re-install kapacitor via npm: npm install -g @kurrent/kapacitor"
            );
            return 1;
        }

        // Per-skill preflight runs BEFORE writing hooks so a packaging defect
        // (top-level skills/ present but an individual skill folder missing)
        // can't leave the user with hooks installed and skills not. This is the
        // atomicity guarantee from AI-676 — either everything installs or nothing.
        var missingSkills = AgentsSkillsInstaller.SourceNames
            .Where(name => !Directory.Exists(Path.Combine(skillsSource, name)))
            .ToList();
        if (missingSkills.Count > 0) {
            await Console.Error.WriteLineAsync(
                $"Cannot install Codex plugin: missing skill folder(s) under {skillsSource}: "
                + string.Join(", ", missingSkills)
                + ". Re-install kapacitor via npm: npm install -g @kurrent/kapacitor");
            return 1;
        }

        if (!InstallCodexHooks(hooksPath)) {
            await Console.Error.WriteLineAsync("Could not write Codex hooks file.");

            return 1;
        }

        await Console.Out.WriteLineAsync($"Codex hooks installed ({scope}: {hooksPath})");
        await Console.Out.WriteLineAsync(
            "Next: run /hooks inside Codex and trust each kapacitor entry — " +
            "Codex won't execute hooks until each is explicitly trusted."
        );

        // Skills are user-scoped only. Written to ~/.agents/skills/ so they
        // work across Codex and other compatible agents.
        if (!AgentsSkillsInstaller.Install(skillsSource, AgentsPaths.UserSkillsDir)) {
            await Console.Error.WriteLineAsync("Could not install agent skills.");
            return 1;
        }

        await Console.Out.WriteLineAsync($"Agent skills installed (user: {AgentsPaths.UserSkillsDir})");

        AgentsSkillsInstaller.CleanLegacyCodexSkills(Path.Combine(CodexPaths.Home, "skills"));

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

        var agentsRemoved = AgentsSkillsInstaller.Remove(AgentsPaths.UserSkillsDir);

        if (agentsRemoved) {
            await Console.Out.WriteLineAsync($"Agent skills removed (user: {AgentsPaths.UserSkillsDir})");
        }

        var legacyRemoved = AgentsSkillsInstaller.CleanLegacyCodexSkills(Path.Combine(CodexPaths.Home, "skills"));

        if (!hooksRemoved && !agentsRemoved && !legacyRemoved) {
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

    static int PrintUsage() {
        Console.Error.WriteLine("Usage: kapacitor plugin <install|remove> [--project] [--codex|--skills]");

        return 1;
    }
}
