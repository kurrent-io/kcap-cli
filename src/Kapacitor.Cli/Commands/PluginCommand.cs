using System.Text.Json;
using System.Text.Json.Nodes;
using Kapacitor.Cli.Core;
using Kapacitor.Cli.Core.Cursor;

namespace Kapacitor.Cli.Commands;

public static class PluginCommand {
    static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    const string CodexHookCommand   = "kapacitor codex-hook";
    const string CursorHookCommand  = "kapacitor hook --cursor";

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
        if ((args.Contains("--codex")  && args.Contains("--skills"))
         || (args.Contains("--cursor") && args.Contains("--skills"))
         || (args.Contains("--cursor") && args.Contains("--codex"))) {
            await Console.Error.WriteLineAsync(
                "--cursor, --codex, and --skills are mutually exclusive.");
            return 1;
        }

        if (args.Contains("--skills")) return await InstallSkills(args);
        if (args.Contains("--codex"))  return await InstallCodex(args);
        if (args.Contains("--cursor")) return await InstallCursor(args);
        return await InstallClaude(args);
    }

    static async Task<int> Remove(string[] args) {
        if ((args.Contains("--codex")  && args.Contains("--skills"))
         || (args.Contains("--cursor") && args.Contains("--skills"))
         || (args.Contains("--cursor") && args.Contains("--codex"))) {
            await Console.Error.WriteLineAsync(
                "--cursor, --codex, and --skills are mutually exclusive.");
            return 1;
        }

        if (args.Contains("--skills")) return await RemoveSkills(args);
        if (args.Contains("--codex"))  return await RemoveCodex(args);
        if (args.Contains("--cursor")) return await RemoveCursor(args);
        return await RemoveClaude(args);
    }

    static async Task<int> InstallClaude(string[] args) {
        var scope = args.Contains("--project") ? "project" : "user";

        var settingsPath = scope == "project"
            ? Path.Combine(Environment.CurrentDirectory, ".claude", "settings.local.json")
            : ClaudePaths.UserSettings;

        // --if-installed: refresh-only mode used by the npm postinstall hook.
        // Skip when the user never opted in; short-circuit when the marker
        // already matches the current CLI version.
        var refreshOnly = args.Contains("--if-installed");

        if (refreshOnly && !ClaudePluginInstaller.IsInstalled(settingsPath)) {
            return 0;
        }

        if (refreshOnly &&
            ClaudePluginInstaller.ReadMarker(settingsPath) == KapacitorVersion.Current()) {
            return 0;
        }

        var pluginPath = SetupCommand.ResolvePluginPath();

        if (pluginPath is null) {
            if (refreshOnly) return 0;
            await Console.Error.WriteLineAsync("Plugin directory not found. Re-install kapacitor via npm:");
            await Console.Error.WriteLineAsync("  npm install -g @kurrent/kapacitor");

            return 1;
        }

        var installed = SetupCommand.InstallPlugin(settingsPath, pluginPath);

        if (installed) {
            await Console.Out.WriteLineAsync(refreshOnly
                ? $"Plugin refreshed ({scope}: {settingsPath})"
                : $"Plugin installed ({scope}: {settingsPath})");
        } else {
            if (refreshOnly) return 0;
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
            var outcome = RemoveClaudePlugin(settingsPath);

            switch (outcome) {
                case ClaudeRemovalOutcome.Removed:
                    await Console.Out.WriteLineAsync($"Plugin removed ({scope}: {settingsPath})");
                    break;
                case ClaudeRemovalOutcome.NotInstalled:
                    await Console.Out.WriteLineAsync("Plugin was not installed.");
                    break;
                case ClaudeRemovalOutcome.Malformed:
                    await Console.Out.WriteLineAsync("Nothing to remove.");
                    break;
            }

            return 0;
        } catch (Exception ex) {
            await Console.Error.WriteLineAsync($"Could not update settings: {ex.Message}");

            return 1;
        }
    }

    /// <summary>
    /// Removes kapacitor's marketplace + enabledPlugins entries (including the
    /// legacy <c>kurrent</c> keys) from the Claude Code settings file at
    /// <paramref name="settingsPath"/>, and deletes the version marker. Non-kapacitor
    /// settings are preserved. Throws on I/O failure so callers can decide how to
    /// report it; returns <see cref="ClaudeRemovalOutcome.NotInstalled"/> when the
    /// file exists but contains no kapacitor entries.
    /// </summary>
    public static ClaudeRemovalOutcome RemoveClaudePlugin(string settingsPath) {
        if (!File.Exists(settingsPath)) return ClaudeRemovalOutcome.NotInstalled;

        var text = File.ReadAllText(settingsPath);

        if (JsonNode.Parse(text) is not JsonObject root) return ClaudeRemovalOutcome.Malformed;

        var changed = false;

        if (root["enabledPlugins"] is JsonObject enabled) {
            changed |= enabled.Remove("kapacitor@kapacitor");
            changed |= enabled.Remove("kapacitor@kurrent");
        }

        if (root["extraKnownMarketplaces"] is JsonObject marketplaces) {
            changed |= marketplaces.Remove("kapacitor");
            changed |= marketplaces.Remove("kurrent");
        }

        if (!changed) return ClaudeRemovalOutcome.NotInstalled;

        File.WriteAllText(settingsPath, root.ToJsonString(WriteOpts));
        ClaudePluginInstaller.DeleteMarker(settingsPath);

        return ClaudeRemovalOutcome.Removed;
    }

    public enum ClaudeRemovalOutcome {
        Removed,
        NotInstalled,
        Malformed
    }

    static async Task<int> InstallSkills(string[] args) {
        // --if-installed: only refresh when a marker file shows the user has
        // previously installed skills. Used by the npm postinstall hook to
        // keep existing installs up to date without forcing skills onto users
        // who haven't run `kapacitor setup` yet.
        var refreshOnly = args.Contains("--if-installed");

        if (refreshOnly && !AgentsSkillsInstaller.IsInstalled(AgentsPaths.UserSkillsDir)) {
            return 0;
        }

        // Fast path: marker already matches the current build, no point
        // re-copying every skill on a same-version reinstall (e.g. `npm
        // install -g` of the version already on disk).
        if (refreshOnly &&
            AgentsSkillsInstaller.ReadMarker(AgentsPaths.UserSkillsDir) ==
                AgentsSkillsInstaller.CurrentVersion()) {
            return 0;
        }

        var pluginPath = SetupCommand.ResolvePluginPath();

        if (pluginPath is null) {
            if (refreshOnly) return 0;
            await Console.Error.WriteLineAsync(
                "Cannot install agent skills: kapacitor plugin folder not found. " +
                "Re-install kapacitor via npm: npm install -g @kurrent/kapacitor");
            return 1;
        }

        var skillsSource = Path.Combine(pluginPath, "skills");

        if (!Directory.Exists(skillsSource)) {
            if (refreshOnly) return 0;
            await Console.Error.WriteLineAsync(
                $"Cannot install agent skills: 'skills' folder missing from {pluginPath}. " +
                "Re-install kapacitor via npm: npm install -g @kurrent/kapacitor");
            return 1;
        }

        if (!AgentsSkillsInstaller.Install(skillsSource, AgentsPaths.UserSkillsDir)) {
            if (refreshOnly) return 0;
            await Console.Error.WriteLineAsync("Could not install agent skills.");
            return 1;
        }

        await Console.Out.WriteLineAsync(refreshOnly
            ? $"Agent skills refreshed (user: {AgentsPaths.UserSkillsDir})"
            : $"Agent skills installed (user: {AgentsPaths.UserSkillsDir})");

        AgentsSkillsInstaller.CleanLegacyCodexSkills(Path.Combine(CodexPaths.Home, "skills"));
        return 0;
    }

    static async Task<int> RemoveSkills(string[] _) {
        var agents = AgentsSkillsInstaller.Remove(AgentsPaths.UserSkillsDir);

        if (agents.RemovedAny) {
            await Console.Out.WriteLineAsync($"Agent skills removed (user: {AgentsPaths.UserSkillsDir})");
        }

        var legacy = AgentsSkillsInstaller.CleanLegacyCodexSkills(Path.Combine(CodexPaths.Home, "skills"));

        if (agents.HadErrors || legacy.HadErrors) {
            await Console.Out.WriteLineAsync("Removal incomplete — see errors above.");
            return 0;
        }

        if (!agents.RemovedAny && !legacy.RemovedAny) {
            await Console.Out.WriteLineAsync("Nothing to remove — agent skills were not installed.");
        }

        return 0;
    }

    static async Task<int> InstallCodex(string[] args) {
        var scope = args.Contains("--project") ? "project" : "user";

        var hooksPath = scope == "project"
            ? Path.Combine(Environment.CurrentDirectory, ".codex", "hooks.json")
            : CodexPaths.UserHooksJson;

        // --if-installed: refresh-only mode used by the npm postinstall hook.
        // Skip when the user never opted in; short-circuit when the marker
        // already matches the current CLI version. Skills are NOT touched
        // here — `--skills --if-installed` is its own postinstall call.
        var refreshOnly = args.Contains("--if-installed");

        if (refreshOnly && !CodexHooksInstaller.IsInstalled(hooksPath)) {
            return 0;
        }

        if (refreshOnly &&
            CodexHooksInstaller.ReadMarker(hooksPath) == KapacitorVersion.Current()) {
            return 0;
        }

        if (refreshOnly) {
            // Hooks-only refresh: rewrite the kapacitor entries in hooks.json,
            // stamp the marker, exit. No skills, no plugin folder needed.
            if (!InstallCodexHooks(hooksPath)) {
                // Never fail the npm install path.
                return 0;
            }

            await Console.Out.WriteLineAsync($"Codex hooks refreshed ({scope}: {hooksPath})");
            return 0;
        }

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

        var agents = AgentsSkillsInstaller.Remove(AgentsPaths.UserSkillsDir);

        if (agents.RemovedAny) {
            await Console.Out.WriteLineAsync($"Agent skills removed (user: {AgentsPaths.UserSkillsDir})");
        }

        var legacy = AgentsSkillsInstaller.CleanLegacyCodexSkills(Path.Combine(CodexPaths.Home, "skills"));

        if (agents.HadErrors || legacy.HadErrors) {
            await Console.Out.WriteLineAsync("Removal incomplete — see errors above.");
            return 0;
        }

        if (!hooksRemoved && !agents.RemovedAny && !legacy.RemovedAny) {
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

            CodexHooksInstaller.WriteMarker(hooksPath);

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
                CodexHooksInstaller.DeleteMarker(hooksPath);
            }

            return changed;
        } catch {
            return false;
        }
    }

    static async Task<int> InstallCursor(string[] args) {
        var hooksPath = GetArg(args, "--cursor-hooks-path") ?? CursorPaths.UserHooksJson();

        var refreshOnly = args.Contains("--if-installed");

        if (refreshOnly && !CursorHooksInstaller.IsInstalled(hooksPath)) return 0;
        if (refreshOnly &&
            CursorHooksInstaller.ReadMarker(hooksPath) == KapacitorVersion.Current()) {
            return 0;
        }

        // PATH precheck on the non-postinstall path. hooks.json writes the bare
        // `kapacitor hook --cursor` command; we must verify Cursor will actually
        // find it. Skip the precheck on the postinstall (--if-installed) path so
        // an in-flight npm install doesn't fail just because the new symlink
        // isn't on the child process's PATH yet.
        if (!refreshOnly && !AgentDetector.IsInstalled("kapacitor")) {
            await Console.Error.WriteLineAsync(
                "Cannot install Cursor hooks: 'kapacitor' is not on PATH. "
                + "Re-install kapacitor via npm: npm install -g @kurrent/kapacitor");
            return 1;
        }

        if (!InstallCursorHooks(hooksPath)) {
            if (refreshOnly) return 0;
            await Console.Error.WriteLineAsync("Could not write Cursor hooks file.");
            return 1;
        }

        await Console.Out.WriteLineAsync(refreshOnly
            ? $"Cursor hooks refreshed ({hooksPath})"
            : $"Cursor hooks installed ({hooksPath})");
        return 0;
    }

    static async Task<int> RemoveCursor(string[] args) {
        var hooksPath = GetArg(args, "--cursor-hooks-path") ?? CursorPaths.UserHooksJson();
        if (!File.Exists(hooksPath)) {
            await Console.Out.WriteLineAsync("Nothing to remove — Cursor hooks file not found.");
            return 0;
        }
        var removed = RemoveCursorHooks(hooksPath);
        await Console.Out.WriteLineAsync(removed
            ? $"Cursor hooks removed ({hooksPath})"
            : "Cursor hooks were not installed.");
        return 0;
    }

    /// <summary>
    /// Writes (or merges into) <paramref name="hooksPath"/> a Cursor hooks.json
    /// invoking <c>kapacitor hook --cursor</c> for every event. Preserves
    /// user-authored entries; replaces existing kapacitor entries.
    /// </summary>
    public static bool InstallCursorHooks(string hooksPath) {
        try {
            JsonObject root = [];
            if (File.Exists(hooksPath)) {
                try { if (JsonNode.Parse(File.ReadAllText(hooksPath)) is JsonObject obj) root = obj; }
                catch { /* Malformed — start fresh */ }
            }

            if (root["version"] is null) root["version"] = 1;
            if (root["hooks"] is not JsonObject hooks) { hooks = []; root["hooks"] = hooks; }

            foreach (var evt in CursorHooksParser.CursorHookEvents) {
                var kapacitorEntry = new JsonObject {
                    ["command"] = CursorHookCommand
                };

                if (hooks[evt] is not JsonArray entries) {
                    hooks[evt] = new JsonArray(kapacitorEntry);
                    continue;
                }

                var preserved = new JsonArray();
                foreach (var entry in entries) {
                    if (entry is null) continue;
                    if (!CursorHooksParser.EntryReferencesKapacitorCursorHook(entry)) {
                        preserved.Add(entry.DeepClone());
                    }
                }
                preserved.Add((JsonNode)kapacitorEntry);
                hooks[evt] = preserved;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(hooksPath)!);
            File.WriteAllText(hooksPath, root.ToJsonString(WriteOpts));
            CursorHooksInstaller.WriteMarker(hooksPath);
            return true;
        } catch { return false; }
    }

    /// <summary>
    /// Removes every entry in <paramref name="hooksPath"/> whose command
    /// invokes <c>kapacitor hook --cursor</c>. Other entries are preserved.
    /// Returns true if any entries were removed.
    /// </summary>
    public static bool RemoveCursorHooks(string hooksPath) {
        try {
            if (!File.Exists(hooksPath)) return false;
            if (JsonNode.Parse(File.ReadAllText(hooksPath)) is not JsonObject root) return false;
            if (root["hooks"] is not JsonObject hooks) return false;

            var changed = false;
            foreach (var evt in CursorHooksParser.CursorHookEvents) {
                if (hooks[evt] is not JsonArray entries) continue;
                var preserved = new JsonArray();
                foreach (var entry in entries) {
                    if (entry is null) continue;
                    if (CursorHooksParser.EntryReferencesKapacitorCursorHook(entry)) {
                        changed = true;
                    } else {
                        preserved.Add(entry.DeepClone());
                    }
                }
                hooks[evt] = preserved;
            }

            if (changed) {
                File.WriteAllText(hooksPath, root.ToJsonString(WriteOpts));
                CursorHooksInstaller.DeleteMarker(hooksPath);
            }
            return changed;
        } catch { return false; }
    }

    static string? GetArg(string[] args, string flag) {
        var idx = Array.IndexOf(args, flag);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }

    static int PrintUsage() {
        Console.Error.WriteLine(
            "Usage: kapacitor plugin <install|remove> [--project] [--codex|--cursor|--skills] [--if-installed]");

        return 1;
    }
}
