using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Copilot;
using Capacitor.Cli.Core.Cursor;

namespace Capacitor.Cli.Commands;

public static class PluginCommand {
    static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    const string CodexHookCommand   = "kcap hook --codex";
    const string CursorHookCommand  = "kcap hook --cursor";
    const string CopilotHookCommand = "kcap hook --copilot";

    // PermissionRequest must wait for the dashboard's decision; the daemon-side
    // bridge call is intentionally infinite. 86400s = 24h keeps Codex from
    // killing the hook before the user approves or denies.
    const int PermissionRequestTimeout = 86400;
    const int DefaultHookTimeout       = 30;

    public static async Task<int> HandleAsync(string[] args, PluginEnvironment? env = null) {
        if (args.Length < 2) {
            PrintUsage();

            return 1;
        }

        env ??= PluginEnvironment.FromProcess();

        return args[1] switch {
            "install" => await Install(args, env),
            "remove"  => await Remove(args, env),
            _         => PrintUsage()
        };
    }

    static readonly string[] ExclusiveTargetFlags = ["--codex", "--cursor", "--copilot", "--skills"];

    static bool HasConflictingTargets(string[] args) =>
        ExclusiveTargetFlags.Count(args.Contains) > 1;

    static async Task<int> Install(string[] args, PluginEnvironment env) {
        if (HasConflictingTargets(args)) {
            await env.Stderr.WriteLineAsync(
                "--cursor, --codex, --copilot, and --skills are mutually exclusive."
            );

            return 1;
        }

        if (args.Contains("--skills")) return await InstallSkills(args, env);
        if (args.Contains("--codex")) return await InstallCodex(args, env);
        if (args.Contains("--cursor")) return await InstallCursor(args, env);
        if (args.Contains("--copilot")) return await InstallCopilot(args, env);

        return await InstallClaude(args, env);
    }

    static async Task<int> Remove(string[] args, PluginEnvironment env) {
        if (HasConflictingTargets(args)) {
            await env.Stderr.WriteLineAsync(
                "--cursor, --codex, --copilot, and --skills are mutually exclusive."
            );

            return 1;
        }

        if (args.Contains("--skills")) return await RemoveSkills(args, env);
        if (args.Contains("--codex")) return await RemoveCodex(args, env);
        if (args.Contains("--cursor")) return await RemoveCursor(args, env);
        if (args.Contains("--copilot")) return await RemoveCopilot(args, env);

        return await RemoveClaude(args, env);
    }

    static async Task<int> InstallClaude(string[] args, PluginEnvironment env) {
        var scope = args.Contains("--project") ? "project" : "user";

        var settingsPath = scope == "project"
            ? Path.Combine(Environment.CurrentDirectory, ".claude", "settings.local.json")
            : env.ClaudeUserSettings;

        // --if-installed: refresh-only mode used by the npm postinstall hook.
        // Skip when the user never opted in; short-circuit when the marker
        // already matches the current CLI version.
        var refreshOnly = args.Contains("--if-installed");

        switch (refreshOnly) {
            case true when !ClaudePluginInstaller.IsInstalled(settingsPath):
            case true when
                ClaudePluginInstaller.ReadMarker(settingsPath) == CapacitorVersion.Current():
                return 0;
        }

        var pluginPath = env.ResolvePluginPath();

        if (pluginPath is null) {
            if (refreshOnly) return 0;

            await env.Stderr.WriteLineAsync("Plugin directory not found. Re-install kcap via npm:");
            await env.Stderr.WriteLineAsync("  npm install -g @kurrent/kcap");

            return 1;
        }

        var installed = SetupCommand.InstallPlugin(settingsPath, pluginPath);

        if (installed) {
            await env.Stdout.WriteLineAsync(
                refreshOnly
                    ? $"Plugin refreshed ({scope}: {settingsPath})"
                    : $"Plugin installed ({scope}: {settingsPath})"
            );

            // AI-836: Claude Code only loads hooks at session start. A fresh install from
            // inside a running session won't record it live until the user restarts. The
            // refresh path (npm postinstall) is silent — it isn't an interactive moment and
            // existing sessions already had hooks, so the reminder would be noise.
            if (!refreshOnly) {
                await env.Stdout.WriteLineAsync(
                    "Live recording begins on a new Claude Code session — restart Claude "
                  + "(or run `claude --continue`) for the hooks to take effect."
                );
            }
        } else {
            if (refreshOnly) return 0;

            await env.Stderr.WriteLineAsync("Could not update settings file.");

            return 1;
        }

        return 0;
    }

    static async Task<int> RemoveClaude(string[] args, PluginEnvironment env) {
        var scope = args.Contains("--project") ? "project" : "user";

        var settingsPath = scope == "project"
            ? Path.Combine(Environment.CurrentDirectory, ".claude", "settings.local.json")
            : env.ClaudeUserSettings;

        if (!File.Exists(settingsPath)) {
            await env.Stdout.WriteLineAsync("Nothing to remove — settings file not found.");

            return 0;
        }

        try {
            var outcome = RemoveClaudePlugin(settingsPath);

            switch (outcome) {
                case ClaudeRemovalOutcome.Removed:
                    await env.Stdout.WriteLineAsync($"Plugin removed ({scope}: {settingsPath})");

                    break;
                case ClaudeRemovalOutcome.NotInstalled:
                    await env.Stdout.WriteLineAsync("Plugin was not installed.");

                    break;
                case ClaudeRemovalOutcome.Malformed:
                    await env.Stdout.WriteLineAsync("Nothing to remove.");

                    break;
            }

            return 0;
        } catch (Exception ex) {
            await env.Stderr.WriteLineAsync($"Could not update settings: {ex.Message}");

            return 1;
        }
    }

    /// <summary>
    /// Removes kcap's marketplace + enabledPlugins entries (including the
    /// legacy <c>kurrent</c> and pre-rename <c>kapacitor</c> keys) from the
    /// Claude Code settings file at <paramref name="settingsPath"/>, and
    /// deletes the version marker. Non-kcap settings are preserved. Throws
    /// on I/O failure so callers can decide how to report it; returns
    /// <see cref="ClaudeRemovalOutcome.NotInstalled"/> when the file exists
    /// but contains no kcap entries.
    /// </summary>
    public static ClaudeRemovalOutcome RemoveClaudePlugin(string settingsPath) {
        if (!File.Exists(settingsPath)) return ClaudeRemovalOutcome.NotInstalled;

        var text = File.ReadAllText(settingsPath);

        if (JsonNode.Parse(text) is not JsonObject root) return ClaudeRemovalOutcome.Malformed;

        var changed = false;

        if (root["enabledPlugins"] is JsonObject enabled) {
            changed |= enabled.Remove("kcap@kcap");
            changed |= enabled.Remove("kcap@kurrent");
            changed |= enabled.Remove("kapacitor@kapacitor");
            changed |= enabled.Remove("kapacitor@kurrent");
        }

        if (root["extraKnownMarketplaces"] is JsonObject marketplaces) {
            changed |= marketplaces.Remove("kcap");
            changed |= marketplaces.Remove("kurrent");
            changed |= marketplaces.Remove("kapacitor");
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

    static async Task<int> InstallSkills(string[] args, PluginEnvironment env) {
        // --if-installed: only refresh when a marker file shows the user has
        // previously installed skills. Used by the npm postinstall hook to
        // keep existing installs up to date without forcing skills onto users
        // who haven't run `kcap setup` yet.
        var refreshOnly = args.Contains("--if-installed");

        switch (refreshOnly) {
            case true when !AgentsSkillsInstaller.IsInstalled(env.AgentsSkillsDir):
            // Fast path: marker already matches the current build, no point
            // re-copying every skill on a same-version reinstall (e.g. `npm
            // install -g` of the version already on disk).
            case true when
                AgentsSkillsInstaller.ReadMarker(env.AgentsSkillsDir) ==
                AgentsSkillsInstaller.CurrentVersion():
                return 0;
        }

        var pluginPath = env.ResolvePluginPath();

        if (pluginPath is null) {
            if (refreshOnly) return 0;

            await env.Stderr.WriteLineAsync(
                "Cannot install agent skills: kcap plugin folder not found. " +
                "Re-install kcap via npm: npm install -g @kurrent/kcap"
            );

            return 1;
        }

        var skillsSource = Path.Combine(pluginPath, "skills");

        if (!Directory.Exists(skillsSource)) {
            if (refreshOnly) return 0;

            await env.Stderr.WriteLineAsync(
                $"Cannot install agent skills: 'skills' folder missing from {pluginPath}. " +
                "Re-install kcap via npm: npm install -g @kurrent/kcap"
            );

            return 1;
        }

        if (!AgentsSkillsInstaller.Install(skillsSource, env.AgentsSkillsDir)) {
            if (refreshOnly) return 0;

            await env.Stderr.WriteLineAsync("Could not install agent skills.");

            return 1;
        }

        await env.Stdout.WriteLineAsync(
            refreshOnly
                ? $"Agent skills refreshed (user: {env.AgentsSkillsDir})"
                : $"Agent skills installed (user: {env.AgentsSkillsDir})"
        );

        AgentsSkillsInstaller.CleanLegacyCodexSkills(env.LegacyCodexSkills);

        return 0;
    }

    static async Task<int> RemoveSkills(string[] _, PluginEnvironment env) {
        var agents = AgentsSkillsInstaller.Remove(env.AgentsSkillsDir);

        if (agents.RemovedAny) {
            await env.Stdout.WriteLineAsync($"Agent skills removed (user: {env.AgentsSkillsDir})");
        }

        var legacy = AgentsSkillsInstaller.CleanLegacyCodexSkills(env.LegacyCodexSkills);

        if (agents.HadErrors || legacy.HadErrors) {
            await env.Stdout.WriteLineAsync("Removal incomplete — see errors above.");

            return 0;
        }

        if (!agents.RemovedAny && !legacy.RemovedAny) {
            await env.Stdout.WriteLineAsync("Nothing to remove — agent skills were not installed.");
        }

        return 0;
    }

    static async Task<int> InstallCodex(string[] args, PluginEnvironment env) {
        var scope = args.Contains("--project") ? "project" : "user";

        var hooksPath = scope == "project"
            ? Path.Combine(Environment.CurrentDirectory, ".codex", "hooks.json")
            : env.CodexUserHooksJson;

        // --if-installed: refresh-only mode used by the npm postinstall hook.
        // Skip when the user never opted in; short-circuit when the marker
        // already matches the current CLI version. Skills are NOT touched
        // here — `--skills --if-installed` is its own postinstall call.
        var refreshOnly = args.Contains("--if-installed");

        switch (refreshOnly) {
            case true when !CodexHooksInstaller.IsInstalled(hooksPath):
            case true when CodexHooksInstaller.ReadMarker(hooksPath) == CapacitorVersion.Current():
            // Hooks-only refresh: rewrite the kcap entries in hooks.json,
            // stamp the marker, exit. No skills, no plugin folder needed.
            // Never fail the npm install path.
            case true when !InstallCodexHooks(hooksPath):
                return 0;
            case true:
                await env.Stdout.WriteLineAsync($"Codex hooks refreshed ({scope}: {hooksPath})");

                return 0;
        }

        // `--codex` is an atomic hooks AND skills contract. Resolve the
        // skills source BEFORE writing hooks so a missing plugin folder
        // doesn't leave the user with hooks pointing at a binary whose
        // skills never installed.
        var pluginPath = env.ResolvePluginPath();

        if (pluginPath is null) {
            await env.Stderr.WriteLineAsync(
                "Cannot install Codex plugin: kcap plugin folder not found. " +
                "Re-install kcap via npm: npm install -g @kurrent/kcap"
            );

            return 1;
        }

        var skillsSource = Path.Combine(pluginPath, "skills");

        if (!Directory.Exists(skillsSource)) {
            await env.Stderr.WriteLineAsync(
                $"Cannot install Codex plugin: 'skills' folder missing from {pluginPath}. " +
                "Re-install kcap via npm: npm install -g @kurrent/kcap"
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
            await env.Stderr.WriteLineAsync(
                $"Cannot install Codex plugin: missing skill folder(s) under {skillsSource}: "
              + string.Join(", ", missingSkills)
              + ". Re-install kcap via npm: npm install -g @kurrent/kcap"
            );

            return 1;
        }

        if (!InstallCodexHooks(hooksPath)) {
            await env.Stderr.WriteLineAsync("Could not write Codex hooks file.");

            return 1;
        }

        await env.Stdout.WriteLineAsync($"Codex hooks installed ({scope}: {hooksPath})");

        await env.Stdout.WriteLineAsync(
            "Next: Codex will prompt to trust the kcap hooks on its next launch — " +
            "accept once to trust them all (or run /hooks inside Codex to trust them individually)."
        );

        // Skills are user-scoped only. Written to ~/.agents/skills/ so they
        // work across Codex and other compatible agents.
        if (!AgentsSkillsInstaller.Install(skillsSource, env.AgentsSkillsDir)) {
            await env.Stderr.WriteLineAsync("Could not install agent skills.");

            return 1;
        }

        await env.Stdout.WriteLineAsync($"Agent skills installed (user: {env.AgentsSkillsDir})");

        AgentsSkillsInstaller.CleanLegacyCodexSkills(env.LegacyCodexSkills);

        if (scope == "project") {
            await env.Stdout.WriteLineAsync(
                "Note: Codex requires the project's .codex directory to be trusted. " +
                "Run `codex` once in this directory and accept the trust prompt."
            );
        }

        return 0;
    }

    static async Task<int> RemoveCodex(string[] args, PluginEnvironment env) {
        var scope = args.Contains("--project") ? "project" : "user";

        var hooksPath = scope == "project"
            ? Path.Combine(Environment.CurrentDirectory, ".codex", "hooks.json")
            : env.CodexUserHooksJson;

        var hooksRemoved = false;
        var hooksFailed  = false;

        if (File.Exists(hooksPath)) {
            try {
                hooksRemoved = RemoveCodexHooks(hooksPath);
            } catch (Exception ex) {
                await env.Stderr.WriteLineAsync($"Could not update Codex hooks at {hooksPath}: {ex.Message}");
                hooksFailed = true;
            }
        }

        if (hooksRemoved) {
            await env.Stdout.WriteLineAsync($"Codex hooks removed ({scope}: {hooksPath})");
        }

        var agents = AgentsSkillsInstaller.Remove(env.AgentsSkillsDir);

        if (agents.RemovedAny) {
            await env.Stdout.WriteLineAsync($"Agent skills removed (user: {env.AgentsSkillsDir})");
        }

        var legacy = AgentsSkillsInstaller.CleanLegacyCodexSkills(env.LegacyCodexSkills);

        if (hooksFailed || agents.HadErrors || legacy.HadErrors) {
            await env.Stdout.WriteLineAsync("Removal incomplete — see errors above.");

            return 1;
        }

        if (!hooksRemoved && !agents.RemovedAny && !legacy.RemovedAny) {
            await env.Stdout.WriteLineAsync("Nothing to remove — hooks and skills were not installed.");
        }

        return 0;
    }

    /// <summary>
    /// Writes (or merges into) <paramref name="hooksPath"/> a hooks.json that
    /// invokes <c>kcap codex-hook</c> for every Codex event. Existing
    /// non-kcap entries are preserved; existing kcap entries are
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
                var timeout = evt == "PermissionRequest" ? PermissionRequestTimeout : DefaultHookTimeout;

                var kcapEntry = new JsonObject {
                    ["hooks"] = new JsonArray(
                        new JsonObject {
                            ["type"]    = "command",
                            ["command"] = CodexHookCommand,
                            ["timeout"] = timeout
                        }
                    )
                };

                if (hooks[evt] is not JsonArray entries) {
                    hooks[evt] = new JsonArray(kcapEntry);

                    continue;
                }

                var preserved = new JsonArray();

                foreach (var entry in entries) {
                    if (entry is null) continue;

                    if (!CodexHooksParser.EntryReferencesCapacitorCodexHook(entry)) {
                        preserved.Add(entry.DeepClone());
                    }
                }

                preserved.Add((JsonNode)kcapEntry);
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
    /// invokes <c>kcap codex-hook</c>. Other entries are preserved.
    /// Returns true if any entries were removed. Throws on I/O failure
    /// (caller decides how to surface partial writes); returns false only
    /// when there was genuinely nothing to remove.
    /// </summary>
    public static bool RemoveCodexHooks(string hooksPath) {
        if (!File.Exists(hooksPath)) return false;

        if (JsonNode.Parse(File.ReadAllText(hooksPath)) is not JsonObject root) return false;
        if (root["hooks"] is not JsonObject hooks) return false;

        var changed = false;

        foreach (var evt in CodexHooksParser.CodexHookEvents) {
            if (hooks[evt] is not JsonArray entries) continue;

            var preserved = new JsonArray();

            foreach (var entry in entries) {
                if (entry is null) continue;

                if (CodexHooksParser.EntryReferencesCapacitorCodexHook(entry)) {
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
    }

    static async Task<int> InstallCursor(string[] args, PluginEnvironment env) {
        var hooksPath = GetArg(args, "--cursor-hooks-path") ?? env.CursorUserHooksJson;

        var refreshOnly = args.Contains("--if-installed");

        switch (refreshOnly) {
            case true when !CursorHooksInstaller.IsInstalled(hooksPath):
            case true when CursorHooksInstaller.ReadMarker(hooksPath) == CapacitorVersion.Current():
                return 0;
            // PATH precheck on the non-postinstall path. hooks.json writes the bare
            // `kcap hook --cursor` command; we must verify Cursor will actually
            // find it. Skip the precheck on the postinstall (--if-installed) path so
            // an in-flight npm install doesn't fail just because the new symlink
            // isn't on the child process's PATH yet.
            case false when !AgentDetector.IsInstalled("kcap"):
                await env.Stderr.WriteLineAsync(
                    "Cannot install Cursor hooks: 'kcap' is not on PATH. "
                  + "Re-install kcap via npm: npm install -g @kurrent/kcap"
                );

                return 1;
        }

        if (!InstallCursorHooks(hooksPath)) {
            if (refreshOnly) return 0;

            await env.Stderr.WriteLineAsync("Could not write Cursor hooks file.");

            return 1;
        }

        await env.Stdout.WriteLineAsync(
            refreshOnly
                ? $"Cursor hooks refreshed ({hooksPath})"
                : $"Cursor hooks installed ({hooksPath})"
        );

        return 0;
    }

    static async Task<int> RemoveCursor(string[] args, PluginEnvironment env) {
        var hooksPath = GetArg(args, "--cursor-hooks-path") ?? env.CursorUserHooksJson;

        if (!File.Exists(hooksPath)) {
            await env.Stdout.WriteLineAsync("Nothing to remove — Cursor hooks file not found.");

            return 0;
        }

        try {
            var removed = RemoveCursorHooks(hooksPath);

            await env.Stdout.WriteLineAsync(
                removed
                    ? $"Cursor hooks removed ({hooksPath})"
                    : "Cursor hooks were not installed."
            );

            return 0;
        } catch (Exception ex) {
            await env.Stderr.WriteLineAsync($"Could not update Cursor hooks at {hooksPath}: {ex.Message}");

            return 1;
        }
    }

    /// <summary>
    /// Writes (or merges into) <paramref name="hooksPath"/> a Cursor hooks.json
    /// invoking <c>kcap hook --cursor</c> for every event. Preserves
    /// user-authored entries; replaces existing kcap entries.
    /// </summary>
    public static bool InstallCursorHooks(string hooksPath) {
        try {
            JsonObject root = [];

            if (File.Exists(hooksPath)) {
                try {
                    if (JsonNode.Parse(File.ReadAllText(hooksPath)) is JsonObject obj) root = obj;
                } catch {
                    /* Malformed — start fresh */
                }
            }

            if (root["version"] is null) root["version"] = 1;

            if (root["hooks"] is not JsonObject hooks) {
                hooks         = [];
                root["hooks"] = hooks;
            }

            foreach (var evt in CursorHooksParser.CursorHookEvents) {
                var kcapEntry = new JsonObject {
                    ["command"] = CursorHookCommand
                };

                if (hooks[evt] is not JsonArray entries) {
                    hooks[evt] = new JsonArray(kcapEntry);

                    continue;
                }

                var preserved = new JsonArray();

                foreach (var entry in entries) {
                    if (entry is null) continue;

                    if (!CursorHooksParser.EntryReferencesCapacitorCursorHook(entry)) {
                        preserved.Add(entry.DeepClone());
                    }
                }

                preserved.Add((JsonNode)kcapEntry);
                hooks[evt] = preserved;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(hooksPath)!);
            File.WriteAllText(hooksPath, root.ToJsonString(WriteOpts));
            CursorHooksInstaller.WriteMarker(hooksPath);

            return true;
        } catch { return false; }
    }

    static async Task<int> InstallCopilot(string[] args, PluginEnvironment env) {
        var hooksPath = GetArg(args, "--copilot-hooks-path") ?? env.CopilotKcapHooksJson;

        var refreshOnly = args.Contains("--if-installed");

        switch (refreshOnly) {
            case true when !CopilotHooksInstaller.IsInstalled(hooksPath):
            case true when CopilotHooksInstaller.ReadMarker(hooksPath) == CapacitorVersion.Current():
                return 0;
            // Same PATH precheck rationale as Cursor: kcap.json writes the bare
            // `kcap hook --copilot` command, so Copilot must find kcap on PATH.
            // Skipped on the postinstall (--if-installed) path — see InstallCursor.
            case false when !AgentDetector.IsInstalled("kcap"):
                await env.Stderr.WriteLineAsync(
                    "Cannot install Copilot hooks: 'kcap' is not on PATH. "
                  + "Re-install kcap via npm: npm install -g @kurrent/kcap"
                );

                return 1;
        }

        if (!InstallCopilotHooks(hooksPath)) {
            if (refreshOnly) return 0;

            await env.Stderr.WriteLineAsync("Could not write Copilot hooks file.");

            return 1;
        }

        await env.Stdout.WriteLineAsync(
            refreshOnly
                ? $"Copilot hooks refreshed ({hooksPath})"
                : $"Copilot hooks installed ({hooksPath})"
        );

        return 0;
    }

    static async Task<int> RemoveCopilot(string[] args, PluginEnvironment env) {
        var hooksPath = GetArg(args, "--copilot-hooks-path") ?? env.CopilotKcapHooksJson;

        try {
            var removed = RemoveCopilotHooks(hooksPath);

            await env.Stdout.WriteLineAsync(
                removed
                    ? $"Copilot hooks removed ({hooksPath})"
                    : "Nothing to remove — Copilot hooks file not found."
            );

            return 0;
        } catch (Exception ex) {
            await env.Stderr.WriteLineAsync($"Could not remove Copilot hooks at {hooksPath}: {ex.Message}");

            return 1;
        }
    }

    /// <summary>
    /// Writes kcap's own Copilot hooks file. Copilot merges every
    /// <c>*.json</c> under <c>~/.copilot/hooks/</c> at startup, so kcap owns
    /// <c>kcap.json</c> wholesale — no merge with user-authored entries is
    /// needed (unlike the shared-file Cursor/Codex installers). Each entry
    /// embeds the event name in the command because Copilot hook payloads
    /// carry no uniform event-name field (see <see cref="CopilotHookCommand"/>).
    /// </summary>
    public static bool InstallCopilotHooks(string hooksPath) {
        try {
            var hooks = new JsonObject();

            foreach (var evt in CopilotHooksParser.CopilotHookEvents) {
                hooks[evt] = new JsonArray(
                    new JsonObject {
                        ["type"]       = "command",
                        ["command"]    = $"{CopilotHookCommand} --event {evt}",
                        ["timeoutSec"] = DefaultHookTimeout
                    }
                );
            }

            var root = new JsonObject {
                ["version"] = 1,
                ["hooks"]   = hooks
            };

            Directory.CreateDirectory(Path.GetDirectoryName(hooksPath)!);
            File.WriteAllText(hooksPath, root.ToJsonString(WriteOpts));
            CopilotHooksInstaller.WriteMarker(hooksPath);

            return true;
        } catch { return false; }
    }

    /// <summary>
    /// Deletes kcap's Copilot hooks file (kcap owns it wholesale — there are
    /// no user-authored entries to preserve). Returns true when the file
    /// existed; throws on I/O failure.
    /// </summary>
    public static bool RemoveCopilotHooks(string hooksPath) {
        var removed = false;

        if (File.Exists(hooksPath)) {
            File.Delete(hooksPath);
            removed = true;
        }

        CopilotHooksInstaller.DeleteMarker(hooksPath);

        return removed;
    }

    /// <summary>
    /// Removes every entry in <paramref name="hooksPath"/> whose command
    /// invokes <c>kcap hook --cursor</c>. Other entries are preserved.
    /// Returns true if any entries were removed. Throws on I/O failure
    /// (caller decides how to surface partial writes); returns false only
    /// when there was genuinely nothing to remove.
    /// </summary>
    public static bool RemoveCursorHooks(string hooksPath) {
        if (!File.Exists(hooksPath)) return false;
        if (JsonNode.Parse(File.ReadAllText(hooksPath)) is not JsonObject root) return false;
        if (root["hooks"] is not JsonObject hooks) return false;

        var changed = false;

        foreach (var evt in CursorHooksParser.CursorHookEvents) {
            if (hooks[evt] is not JsonArray entries) continue;

            var preserved = new JsonArray();

            foreach (var entry in entries) {
                if (entry is null) continue;

                if (CursorHooksParser.EntryReferencesCapacitorCursorHook(entry)) {
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
    }

    static string? GetArg(string[] args, string flag) {
        var idx = Array.IndexOf(args, flag);

        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }

    static int PrintUsage() {
        Console.Error.WriteLine(
            "Usage: kcap plugin <install|remove> [--project] [--codex|--cursor|--copilot|--skills] [--if-installed]"
        );

        return 1;
    }
}
