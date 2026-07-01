using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Copilot;
using Capacitor.Cli.Core.Cursor;
using Capacitor.Cli.Core.Gemini;
using Capacitor.Cli.Core.Kiro;
using Capacitor.Cli.Core.OpenCode;
using Capacitor.Cli.Core.Pi;

namespace Capacitor.Cli.Commands;

public static class PluginCommand {
    static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    const string CodexHookCommand   = "kcap hook --codex";
    const string CursorHookCommand  = "kcap hook --cursor";
    const string CopilotHookCommand = "kcap hook --copilot";
    const string KiroHookCommand    = "kcap hook --kiro";

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

    static readonly string[] ExclusiveTargetFlags = ["--codex", "--cursor", "--copilot", "--gemini", "--kiro", "--pi", "--opencode", "--skills"];

    static bool HasConflictingTargets(string[] args) =>
        ExclusiveTargetFlags.Count(args.Contains) > 1;

    static async Task<int> Install(string[] args, PluginEnvironment env) {
        if (HasConflictingTargets(args)) {
            await env.Stderr.WriteLineAsync(
                "--cursor, --codex, --copilot, --gemini, --kiro, --pi, --opencode, and --skills are mutually exclusive."
            );

            return 1;
        }

        if (args.Contains("--skills")) return await InstallSkills(args, env);
        if (args.Contains("--codex")) return await InstallCodex(args, env);
        if (args.Contains("--cursor")) return await InstallCursor(args, env);
        if (args.Contains("--copilot")) return await InstallCopilot(args, env);
        if (args.Contains("--gemini")) return await InstallGemini(args, env);
        if (args.Contains("--kiro")) return await InstallKiro(args, env);
        if (args.Contains("--pi")) return await InstallPi(args, env);
        if (args.Contains("--opencode")) return await InstallOpenCode(args, env);

        return await InstallClaude(args, env);
    }

    static async Task<int> Remove(string[] args, PluginEnvironment env) {
        if (HasConflictingTargets(args)) {
            await env.Stderr.WriteLineAsync(
                "--cursor, --codex, --copilot, --gemini, --kiro, --pi, --opencode, and --skills are mutually exclusive."
            );

            return 1;
        }

        if (args.Contains("--skills")) return await RemoveSkills(args, env);
        if (args.Contains("--codex")) return await RemoveCodex(args, env);
        if (args.Contains("--cursor")) return await RemoveCursor(args, env);
        if (args.Contains("--copilot")) return await RemoveCopilot(args, env);
        if (args.Contains("--gemini")) return await RemoveGemini(args, env);
        if (args.Contains("--kiro")) return await RemoveKiro(args, env);
        if (args.Contains("--pi")) return await RemovePi(args, env);
        if (args.Contains("--opencode")) return await RemoveOpenCode(args, env);

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

        // AI-794 — enable Codex sandbox network access so the skills just installed can
        // reach the Capacitor server. Opt out with --skip-codex-network-access. The
        // --if-installed refresh path returns earlier, so npm postinstall never flips this.
        if (!args.Contains("--skip-codex-network-access"))
            await EnableCodexNetworkAccessAsync(env);

        // Register the kcap MCP servers in ~/.codex/config.toml so Codex CLI picks them up
        // with no manual TOML edit (the plugin descriptor path alone isn't enough — many
        // users never run `codex plugin add`). Non-destructive + idempotent. `kcap-flows`
        // stays Claude-only (AI-1056). The --if-installed refresh returns earlier, so npm
        // postinstall never touches config.toml here.
        await RegisterCodexMcpServersAsync(env);

        if (scope == "project") {
            await env.Stdout.WriteLineAsync(
                "Note: Codex requires the project's .codex directory to be trusted. " +
                "Run `codex` once in this directory and accept the trust prompt."
            );
        }

        return 0;
    }

    /// <summary>
    /// AI-794 — turn on Codex's <c>workspace-write</c> sandbox network access, constrained
    /// to the Capacitor server(s) of every configured profile, so kcap skills can reach the
    /// server. Never fails the install: a write error is a warning, not an error code.
    /// </summary>
    static async Task EnableCodexNetworkAccessAsync(PluginEnvironment env) {
        var profiles = await AppConfig.LoadProfileConfig();
        var domains  = CodexConfigToml.BuildAllowDomains(profiles.Profiles.Values.Select(p => p.ServerUrl));

        if (domains.Count == 0) {
            await env.Stdout.WriteLineAsync(
                "No Capacitor server configured yet — run `kcap setup` to allow Codex network access for kcap skills.");

            return;
        }

        switch (CodexConfigToml.EnableNetworkAccess(domains, env.CodexConfigTomlPath)) {
            case CodexConfigToml.Change.Updated:
                await env.Stdout.WriteLineAsync($"Codex sandbox network access enabled for kcap ({env.CodexConfigTomlPath}).");
                break;
            case CodexConfigToml.Change.Unchanged:
                await env.Stdout.WriteLineAsync("Codex sandbox already allows network access — no change needed.");
                break;
            default:
                await env.Stderr.WriteLineAsync(
                    $"Warning: could not update {env.CodexConfigTomlPath} — enable Codex sandbox network access manually (see README).");
                break;
        }
    }

    /// <summary>
    /// Registers the kcap MCP servers (kcap-review, kcap-sessions) in
    /// <c>~/.codex/config.toml</c> so Codex CLI loads them without a manual TOML edit.
    /// Never fails the install: a write error is a warning, not an error code.
    /// </summary>
    static async Task RegisterCodexMcpServersAsync(PluginEnvironment env) {
        switch (CodexConfigToml.RegisterKcapMcpServers(env.CodexConfigTomlPath)) {
            case CodexConfigToml.Change.Updated:
                await env.Stdout.WriteLineAsync($"Codex MCP servers registered: kcap-review, kcap-sessions ({env.CodexConfigTomlPath}).");
                break;
            case CodexConfigToml.Change.Unchanged:
                await env.Stdout.WriteLineAsync("Codex MCP servers already registered — no change needed.");
                break;
            default:
                await env.Stderr.WriteLineAsync(
                    $"Warning: could not register Codex MCP servers in {env.CodexConfigTomlPath} — see README to add them manually.");
                break;
        }
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

        // Remove the kcap MCP server entries we wrote to config.toml. These are kcap-owned
        // (unlike the sandbox network policy, which is the user's security posture and is
        // deliberately left in place on uninstall).
        var mcpChange = CodexConfigToml.UnregisterKcapMcpServers(env.CodexConfigTomlPath);
        var mcpFailed = mcpChange == CodexConfigToml.Change.Failed;

        if (mcpChange == CodexConfigToml.Change.Updated) {
            await env.Stdout.WriteLineAsync($"Codex MCP servers removed ({env.CodexConfigTomlPath})");
        } else if (mcpFailed) {
            await env.Stderr.WriteLineAsync($"Could not update {env.CodexConfigTomlPath} to remove Codex MCP servers.");
        }

        var agents = AgentsSkillsInstaller.Remove(env.AgentsSkillsDir);

        if (agents.RemovedAny) {
            await env.Stdout.WriteLineAsync($"Agent skills removed (user: {env.AgentsSkillsDir})");
        }

        var legacy = AgentsSkillsInstaller.CleanLegacyCodexSkills(env.LegacyCodexSkills);

        if (hooksFailed || mcpFailed || agents.HadErrors || legacy.HadErrors) {
            await env.Stdout.WriteLineAsync("Removal incomplete — see errors above.");

            return 1;
        }

        if (!hooksRemoved && mcpChange != CodexConfigToml.Change.Updated && !agents.RemovedAny && !legacy.RemovedAny) {
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

    // ── Pi (badlogic/pi-mono): a TypeScript extension, not a hooks.json ──────

    static async Task<int> InstallPi(string[] args, PluginEnvironment env) {
        var extensionPath = GetArg(args, "--pi-extension-path") ?? env.PiKcapExtension;

        var refreshOnly = args.Contains("--if-installed");

        switch (refreshOnly) {
            case true when !PiExtensionInstaller.IsInstalled(extensionPath):
            case true when PiExtensionInstaller.ReadMarker(extensionPath) == CapacitorVersion.Current():
                return 0;
            // The extension shells out to the bare `kcap hook --pi` command, so
            // pi (and therefore kcap) must find kcap on PATH. Skipped on the
            // postinstall (--if-installed) refresh path, like the other vendors.
            case false when !AgentDetector.IsInstalled("kcap"):
                await env.Stderr.WriteLineAsync(
                    "Cannot install the Pi extension: 'kcap' is not on PATH. "
                  + "Re-install kcap via npm: npm install -g @kurrent/kcap"
                );

                return 1;
        }

        if (!PiExtensionInstaller.Install(extensionPath)) {
            if (refreshOnly) return 0;

            await env.Stderr.WriteLineAsync("Could not write the Pi extension file.");

            return 1;
        }

        await env.Stdout.WriteLineAsync(
            refreshOnly
                ? $"Pi extension refreshed ({extensionPath})"
                : $"Pi extension installed ({extensionPath})"
        );

        return 0;
    }

    static async Task<int> RemovePi(string[] args, PluginEnvironment env) {
        var extensionPath = GetArg(args, "--pi-extension-path") ?? env.PiKcapExtension;

        try {
            var removed = PiExtensionInstaller.Remove(extensionPath);

            await env.Stdout.WriteLineAsync(
                removed
                    ? $"Pi extension removed ({extensionPath})"
                    : "Nothing to remove — Pi extension file not found."
            );

            return 0;
        } catch (Exception ex) {
            await env.Stderr.WriteLineAsync($"Could not remove the Pi extension at {extensionPath}: {ex.Message}");

            return 1;
        }
    }

    // ── OpenCode (SST): a TypeScript plugin, not a hooks.json (AI-919) ───────

    static async Task<int> InstallOpenCode(string[] args, PluginEnvironment env) {
        var pluginPath = GetArg(args, "--opencode-plugin-path") ?? env.OpenCodeKcapPlugin;

        var refreshOnly = args.Contains("--if-installed");

        switch (refreshOnly) {
            case true when !OpenCodeExtensionInstaller.IsInstalled(pluginPath):
            case true when OpenCodeExtensionInstaller.ReadMarker(pluginPath) == CapacitorVersion.Current():
                return 0;
            // The plugin shells out to the bare `kcap hook --opencode` command, so
            // OpenCode (and therefore kcap) must find kcap on PATH. Skipped on the
            // postinstall (--if-installed) refresh path, like the other vendors.
            case false when !AgentDetector.IsInstalled("kcap"):
                await env.Stderr.WriteLineAsync(
                    "Cannot install the OpenCode plugin: 'kcap' is not on PATH. "
                  + "Re-install kcap via npm: npm install -g @kurrent/kcap"
                );

                return 1;
        }

        if (!OpenCodeExtensionInstaller.Install(pluginPath)) {
            if (refreshOnly) return 0;

            await env.Stderr.WriteLineAsync("Could not write the OpenCode plugin file.");

            return 1;
        }

        await env.Stdout.WriteLineAsync(
            refreshOnly
                ? $"OpenCode plugin refreshed ({pluginPath})"
                : $"OpenCode plugin installed ({pluginPath})"
        );

        return 0;
    }

    static async Task<int> RemoveOpenCode(string[] args, PluginEnvironment env) {
        var pluginPath = GetArg(args, "--opencode-plugin-path") ?? env.OpenCodeKcapPlugin;

        try {
            var removed = OpenCodeExtensionInstaller.Remove(pluginPath);

            await env.Stdout.WriteLineAsync(
                removed
                    ? $"OpenCode plugin removed ({pluginPath})"
                    : "Nothing to remove — OpenCode plugin file not found."
            );

            return 0;
        } catch (Exception ex) {
            await env.Stderr.WriteLineAsync($"Could not remove the OpenCode plugin at {pluginPath}: {ex.Message}");

            return 1;
        }
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

    const string KiroAgentName = "kcap";
    const string KiroBinary    = "kiro-cli";

    static async Task<int> InstallKiro(string[] args, PluginEnvironment env) {
        var agentPath = GetArg(args, "--kiro-agent-path") ?? env.KiroKcapAgentJson;

        var refreshOnly = args.Contains("--if-installed");

        switch (refreshOnly) {
            case true when !KiroHooksInstaller.IsInstalled(agentPath):
            case true when KiroHooksInstaller.ReadMarker(agentPath) == CapacitorVersion.Current():
                return 0;
            // The agent runs the bare `kcap hook --kiro` command, so kcap must be on PATH.
            case false when !AgentDetector.IsInstalled("kcap"):
                await env.Stderr.WriteLineAsync(
                    "Cannot install Kiro hooks: 'kcap' is not on PATH. "
                  + "Re-install kcap via npm: npm install -g @kurrent/kcap"
                );

                return 1;
        }

        if (!InstallKiroHooks(agentPath)) {
            if (refreshOnly) return 0;

            await env.Stderr.WriteLineAsync(
                $"Could not set up the Kiro '{KiroAgentName}' agent. Is '{KiroBinary}' on PATH? "
              + "(it's needed to clone your current default agent so tool access is preserved.)"
            );

            return 1;
        }

        var clonedFrom = KiroHooksInstaller.ReadPreviousDefault(agentPath) ?? "your default agent";

        await env.Stdout.WriteLineAsync(
            refreshOnly
                ? $"Kiro hooks refreshed ({agentPath})"
                : $"Kiro hooks installed — '{KiroAgentName}' (cloned from '{clonedFrom}') is now your default "
                + "Kiro agent, so every session is captured. Restart kiro-cli to pick it up; "
                + "undo with: kcap plugin remove --kiro"
        );

        return 0;
    }

    static async Task<int> RemoveKiro(string[] args, PluginEnvironment env) {
        var agentPath    = GetArg(args, "--kiro-agent-path")    ?? env.KiroKcapAgentJson;
        var settingsPath = GetArg(args, "--kiro-settings-path") ?? KiroSettingsPathFor(agentPath);

        try {
            // Restore the default agent kcap replaced (recorded at install time).
            // If kcap is currently the default and the restore write FAILS, abort
            // before deleting kcap.json / the marker — otherwise chat.defaultAgent
            // is left pointing at a deleted agent and the recorded previous default
            // (marker line 2) is gone, so a retry can't recover. Leaving both in
            // place keeps `kcap plugin remove --kiro` retryable.
            var previousDefault = KiroHooksInstaller.ReadPreviousDefault(agentPath) ?? "kiro_default";
            if (KiroSettings.ReadDefaultAgent(settingsPath) == KiroAgentName
             && !KiroSettings.SetDefaultAgent(settingsPath, previousDefault)) {
                await env.Stderr.WriteLineAsync(
                    $"Could not restore your default Kiro agent to '{previousDefault}' in {settingsPath}. "
                  + "Left kcap.json in place so you can retry — fix the settings file and re-run: "
                  + "kcap plugin remove --kiro"
                );

                return 1;
            }

            var removed = RemoveKiroHooks(agentPath);

            await env.Stdout.WriteLineAsync(
                removed
                    ? $"Kiro hooks removed; default agent restored to '{previousDefault}' ({agentPath})"
                    : "Nothing to remove — Kiro agent hooks file not found."
            );

            return 0;
        } catch (Exception ex) {
            await env.Stderr.WriteLineAsync($"Could not remove Kiro hooks at {agentPath}: {ex.Message}");

            return 1;
        }
    }

    /// <summary>
    /// Sets up transparent Kiro capture; true on success. Kiro hooks fire only
    /// for the ACTIVE agent and there is no global hook, so capture requires
    /// making kcap the default agent — and a minimal agent loses tool access, so
    /// we clone the current default (preserving its tools/prompt) via
    /// <c>kiro-cli agent create --from</c>, merge our hook in, and flip
    /// <c>chat.defaultAgent</c> to kcap. The replaced default is recorded in the
    /// marker for <c>plugin remove --kiro</c> to restore. Idempotent.
    /// </summary>
    public static bool InstallKiroHooks(string agentJsonPath) {
        try {
            var settingsPath    = KiroSettingsPathFor(agentJsonPath);
            var currentDefault  = KiroSettings.ReadDefaultAgent(settingsPath) ?? "kiro_default";
            var alreadyKcap     = currentDefault == KiroAgentName;

            // The default we'll restore on remove: the real prior default on a
            // fresh install; the one already recorded on a re-install (so we don't
            // overwrite it with "kcap").
            var recordedDefault = alreadyKcap
                ? KiroHooksInstaller.ReadPreviousDefault(agentJsonPath) ?? "kiro_default"
                : currentDefault;

            // Clone the current default into the kcap agent (kiro-cli writes it to
            // the global agents dir, preserving tools/prompt). Skipped if kcap exists.
            if (!File.Exists(agentJsonPath)) {
                if (!AgentDetector.IsInstalled(KiroBinary)) return false;
                if (RunKiroCli("agent", "create", KiroAgentName, "--from", recordedDefault) != 0 || !File.Exists(agentJsonPath))
                    return false;
            }

            if (!InjectKiroHooksIntoAgent(agentJsonPath)) return false;

            // Flip the default FIRST; only stamp the marker once it succeeds.
            // Otherwise a failed settings flip (e.g. a malformed shared settings
            // file, which SetDefaultAgent now fails closed on) would leave a marker
            // that makes IsInstalled / --if-installed treat the broken install as
            // done — so the next refresh would skip it and kcap would never become
            // the default. With no marker, the next --if-installed refresh retries.
            if (!KiroSettings.SetDefaultAgent(settingsPath, KiroAgentName)) return false;

            // The marker's line 2 is the ONLY record of the replaced default, so a
            // silent failure here would let `remove --kiro` restore the wrong agent
            // (and a later --if-installed refresh re-stamp a bogus previous default).
            // Treat it as part of the atomic install: on failure roll the default
            // back and report failure rather than a success we can't undo.
            if (!KiroHooksInstaller.WriteMarker(agentJsonPath, recordedDefault)) {
                // If the rollback ALSO fails (e.g. the shared settings file is locked
                // or became malformed between writes) the prior default can't be
                // recovered automatically — chat.defaultAgent may still be `kcap`
                // with no marker. Surface a DISTINCT, actionable message naming the
                // previous default + paths so the user can restore it by hand,
                // rather than returning the same opaque false as "kiro-cli missing".
                if (!KiroSettings.SetDefaultAgent(settingsPath, recordedDefault)) {
                    Console.Error.WriteLine(
                        $"[kcap] Kiro install could not be completed OR rolled back. "
                      + $"chat.defaultAgent may still be '{KiroAgentName}' with no install marker. "
                      + $"Your previous default agent was '{recordedDefault}' — restore it manually "
                      + $"(set chat.defaultAgent in {settingsPath}) and delete {agentJsonPath}, "
                      + $"then re-run `kcap plugin install --kiro`."
                    );
                }
                return false;
            }

            return true;
        } catch {
            return false;
        }
    }

    /// <summary>
    /// Merges kcap's hook(s) into an existing Kiro agent file's <c>hooks</c> block,
    /// preserving the cloned agent's tools/prompt/etc. Each entry runs the bare
    /// <c>kcap hook --kiro --event NAME</c>. Idempotent (overwrites kcap's events).
    /// Does NOT touch the marker — the caller owns the previous-default record.
    /// </summary>
    public static bool InjectKiroHooksIntoAgent(string agentJsonPath) {
        try {
            if (!File.Exists(agentJsonPath)) return false;
            if (JsonNode.Parse(File.ReadAllText(agentJsonPath)) is not JsonObject root) return false;

            var hooks = root["hooks"] as JsonObject ?? new JsonObject();
            foreach (var evt in KiroHooksParser.KiroHookEvents)
                hooks[evt] = new JsonArray(new JsonObject { ["command"] = $"{KiroHookCommand} --event {evt}" });
            root["hooks"] = hooks;

            File.WriteAllText(agentJsonPath, root.ToJsonString(WriteOpts));
            return true;
        } catch { return false; }
    }

    /// <summary>Derives <c>~/.kiro/settings/cli.json</c> from <c>~/.kiro/agents/kcap.json</c>.</summary>
    static string KiroSettingsPathFor(string agentJsonPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(agentJsonPath)!)!, "settings", "cli.json");

    static int RunKiroCli(params string[] arguments) {
        try {
            // ArgumentList (not a concatenated string) so a default-agent name with
            // whitespace/quotes survives as ONE argument — `ProcessStartInfo(file,
            // string)` would split "My Agent" into two args and break the clone.
            var psi = new ProcessStartInfo(KiroBinary) {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            foreach (var arg in arguments) psi.ArgumentList.Add(arg);
            // `kiro-cli agent create --from` opens $EDITOR on the new agent file and
            // blocks until it's closed — fatal for an unattended install, and Kiro
            // has no --no-edit flag. Point the editor at a no-op so the clone is
            // written and the command returns immediately: `true` exits 0 without
            // touching the file. Override both EDITOR and VISUAL — Kiro falls back to
            // its built-in vi when they're unset.
            psi.Environment["EDITOR"] = "true";
            psi.Environment["VISUAL"] = "true";

            using var p = Process.Start(psi);
            if (p is null) return -1;

            // WaitForExit FIRST so the 60s bound actually applies. Reading a stream
            // to end blocks until the child closes it, so draining before the wait
            // would hang forever if the child stalls (e.g. an editor we failed to
            // suppress). Output here is tiny, so the OS pipe buffer holds it until we
            // drain after exit; on timeout we kill the whole tree (incl. any editor).
            if (!p.WaitForExit(60_000)) {
                try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return -1;
            }

            _ = p.StandardOutput.ReadToEnd();
            _ = p.StandardError.ReadToEnd();
            return p.ExitCode;
        } catch {
            return -1;
        }
    }

    /// <summary>
    /// Deletes kcap's Kiro agent-hooks file (kcap owns it wholesale). Returns
    /// true when the file existed; throws on I/O failure.
    /// </summary>
    public static bool RemoveKiroHooks(string agentJsonPath) {
        var removed = false;

        if (File.Exists(agentJsonPath)) {
            File.Delete(agentJsonPath);
            removed = true;
        }

        KiroHooksInstaller.DeleteMarker(agentJsonPath);

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

    static async Task<int> InstallGemini(string[] args, PluginEnvironment env) {
        var settingsPath = GetArg(args, "--gemini-settings-path") ?? env.GeminiSettingsJson;

        var refreshOnly = args.Contains("--if-installed");

        switch (refreshOnly) {
            case true when !GeminiHooksInstaller.IsInstalled(settingsPath):
            case true when GeminiHooksInstaller.ReadMarker(settingsPath) == CapacitorVersion.Current():
                return 0;
            // Gemini's settings.json writes the bare `kcap hook --gemini` command;
            // verify Gemini will actually find kcap on PATH. Skip on the
            // postinstall (--if-installed) refresh, same as the Cursor branch.
            case false when !AgentDetector.IsInstalled("kcap"):
                await env.Stderr.WriteLineAsync(
                    "Cannot install Gemini hooks: 'kcap' is not on PATH. "
                  + "Re-install kcap via npm: npm install -g @kurrent/kcap"
                );

                return 1;
        }

        if (!InstallGeminiHooks(settingsPath)) {
            if (refreshOnly) return 0;

            await env.Stderr.WriteLineAsync(
                $"Could not install Gemini hooks. If {settingsPath} exists, make sure it is valid JSON — "
              + "kcap leaves an unparseable settings.json untouched rather than overwrite your settings. "
              + "Fix or remove it, then re-run."
            );

            return 1;
        }

        await env.Stdout.WriteLineAsync(
            refreshOnly
                ? $"Gemini hooks refreshed ({settingsPath})"
                : $"Gemini hooks installed ({settingsPath})"
        );

        return 0;
    }

    static async Task<int> RemoveGemini(string[] args, PluginEnvironment env) {
        var settingsPath = GetArg(args, "--gemini-settings-path") ?? env.GeminiSettingsJson;

        if (!File.Exists(settingsPath)) {
            await env.Stdout.WriteLineAsync("Nothing to remove — Gemini settings file not found.");

            return 0;
        }

        try {
            var removed = RemoveGeminiHooks(settingsPath);

            await env.Stdout.WriteLineAsync(
                removed
                    ? $"Gemini hooks removed ({settingsPath})"
                    : "Gemini hooks were not installed."
            );

            return 0;
        } catch (Exception ex) {
            await env.Stderr.WriteLineAsync($"Could not update Gemini hooks at {settingsPath}: {ex.Message}");

            return 1;
        }
    }

    /// <summary>
    /// Merges kcap's command hooks into Gemini's shared
    /// <c>~/.gemini/settings.json</c> for every event in
    /// <see cref="GeminiHooksParser.GeminiHookEvents"/>. Touches ONLY the
    /// <c>hooks</c> block (every other settings key is preserved) and keeps
    /// user-authored hook entries; replaces existing kcap entries.
    /// </summary>
    public static bool InstallGeminiHooks(string settingsPath) {
        try {
            JsonObject root = [];

            if (File.Exists(settingsPath)) {
                // settings.json is SHARED user config, not a kcap-owned hook file.
                // If it exists but won't parse into a JSON object (malformed, empty,
                // or half-written), FAIL CLOSED and leave it untouched. Starting
                // fresh here would have File.WriteAllText overwrite the whole file
                // with only kcap hooks, silently dropping the user's unrelated Gemini
                // settings. (Cursor/Copilot own their dedicated hooks file and may
                // safely start fresh — this shared file must not.)
                JsonNode? parsed;
                try {
                    parsed = JsonNode.Parse(File.ReadAllText(settingsPath));
                } catch (JsonException) {
                    return false;
                }

                if (parsed is not JsonObject obj) return false;
                root = obj;
            }

            if (root["hooks"] is not JsonObject hooks) {
                hooks         = [];
                root["hooks"] = hooks;
            }

            foreach (var evt in GeminiHooksParser.GeminiHookEvents) {
                if (hooks[evt] is not JsonArray entries) {
                    hooks[evt] = new JsonArray(GeminiHooksParser.BuildKcapEntry());

                    continue;
                }

                var preserved = new JsonArray();

                foreach (var entry in entries) {
                    if (entry is null) continue;

                    if (!GeminiHooksParser.EntryReferencesCapacitorGeminiHook(entry)) {
                        preserved.Add(entry.DeepClone());
                    }
                }

                // Cast to JsonNode so this binds to JsonArray.Add(JsonNode?), not
                // the generic Add<T>(T) — the generic trips IL2026/IL3050 under AOT.
                preserved.Add((JsonNode)GeminiHooksParser.BuildKcapEntry());
                hooks[evt] = preserved;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, root.ToJsonString(WriteOpts));
            GeminiHooksInstaller.WriteMarker(settingsPath);

            return true;
        } catch { return false; }
    }

    public static bool RemoveGeminiHooks(string settingsPath) {
        if (!File.Exists(settingsPath)) return false;
        if (JsonNode.Parse(File.ReadAllText(settingsPath)) is not JsonObject root) return false;
        if (root["hooks"] is not JsonObject hooks) return false;

        var changed = false;

        foreach (var evt in GeminiHooksParser.GeminiHookEvents) {
            if (hooks[evt] is not JsonArray entries) continue;

            var preserved = new JsonArray();

            foreach (var entry in entries) {
                if (entry is null) continue;

                if (GeminiHooksParser.EntryReferencesCapacitorGeminiHook(entry)) {
                    changed = true;
                } else {
                    preserved.Add(entry.DeepClone());
                }
            }

            hooks[evt] = preserved;
        }

        if (changed) {
            File.WriteAllText(settingsPath, root.ToJsonString(WriteOpts));
            GeminiHooksInstaller.DeleteMarker(settingsPath);
        }

        return changed;
    }

    static string? GetArg(string[] args, string flag) {
        var idx = Array.IndexOf(args, flag);

        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }

    static int PrintUsage() {
        Console.Error.WriteLine(
            "Usage: kcap plugin <install|remove> [--project] [--codex|--cursor|--copilot|--gemini|--kiro|--pi|--skills] [--if-installed]"
        );

        return 1;
    }
}
