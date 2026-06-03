using Kapacitor.Cli.Core;
using Kapacitor.Cli.Core.Cursor;

namespace Kapacitor.Cli.Commands;

/// <summary>
/// Completely removes kapacitor from the local machine: stops daemons, kills
/// watcher processes, strips kapacitor entries from user-level Claude / Codex /
/// Cursor hook files, removes agent skills (and legacy Codex skills), and
/// deletes the kapacitor config directory.
///
/// With <c>--project</c>, also strips kapacitor entries from the cwd's git-root
/// <c>.claude/settings.local.json</c> and <c>.codex/hooks.json</c>. Project-scope
/// Cursor hooks are not a thing — Cursor only reads its user-scope hooks.json.
/// Per-agent selective cleanup is intentionally out of scope here; this command
/// covers all known agents.
/// </summary>
public static class UninstallCommand {
    public static async Task<int> HandleAsync(string[] args) {
        var skipPrompt     = args.Contains("--yes") || args.Contains("-y");
        var keepConfig     = args.Contains("--keep-config");
        var includeProject = args.Contains("--project");

        string? projectRoot = null;

        if (includeProject) {
            projectRoot = GitRepository.FindRoot(Environment.CurrentDirectory);

            if (projectRoot is null) {
                await Console.Error.WriteLineAsync(
                    $"--project requires a git working tree, but '{Environment.CurrentDirectory}' is not inside one.");
                await Console.Error.WriteLineAsync(
                    "Re-run from inside your repo, or drop --project to only remove user-level configuration.");

                return 1;
            }
        }

        var configDir = ResolveConfigDir();

        await Console.Out.WriteLineAsync("This will remove kapacitor from your machine:");
        await Console.Out.WriteLineAsync("  • Stop any running daemons and watcher processes");
        await Console.Out.WriteLineAsync($"  • Remove kapacitor entries from {ClaudePaths.UserSettings}");
        await Console.Out.WriteLineAsync($"  • Remove kapacitor entries from {CodexPaths.UserHooksJson}");
        await Console.Out.WriteLineAsync($"  • Remove kapacitor entries from {CursorPaths.UserHooksJson()}");
        await Console.Out.WriteLineAsync($"  • Remove agent skills under {AgentsPaths.UserSkillsDir}");

        if (projectRoot is not null) {
            await Console.Out.WriteLineAsync(
                $"  • Remove kapacitor entries from {Path.Combine(projectRoot, ".claude", "settings.local.json")}");
            await Console.Out.WriteLineAsync(
                $"  • Remove kapacitor entries from {Path.Combine(projectRoot, ".codex", "hooks.json")}");
        }

        if (!keepConfig) {
            await Console.Out.WriteLineAsync($"  • Delete the kapacitor config directory ({configDir})");
        }

        if (!skipPrompt) {
            await Console.Out.WriteAsync("Proceed? [y/N] ");
            var reply = await Console.In.ReadLineAsync();

            if (!string.Equals(reply?.Trim(), "y", StringComparison.OrdinalIgnoreCase)) {
                await Console.Out.WriteLineAsync("Cancelled.");

                return 0;
            }
        }

        // Stop daemons first — they hold lock files inside the config dir we're
        // about to delete. --yes silences the multi-daemon confirmation so this
        // works non-interactively.
        await DaemonCommands.HandleAsync(["daemon", "stop", "--yes"]);

        // Kill any orphaned watcher PIDs that the daemon stop didn't catch.
        await CleanupCommand.HandleCleanup();

        // User-level agent integrations. Each remove command is idempotent and
        // no-ops if the target file doesn't exist, so it's safe to call all of
        // them unconditionally without sniffing which agents are installed.
        await PluginCommand.HandleAsync(["plugin", "remove"]);            // Claude
        await PluginCommand.HandleAsync(["plugin", "remove", "--codex"]); // Codex hooks + skills + legacy
        await PluginCommand.HandleAsync(["plugin", "remove", "--cursor"]);

        // Skills are removed by --codex above, but call --skills explicitly in
        // case the user only ever installed Cursor / agent-agnostic skills and
        // never had Codex hooks (the --codex path short-circuits on a missing
        // hooks file).
        await PluginCommand.HandleAsync(["plugin", "remove", "--skills"]);

        if (projectRoot is not null) {
            var claudeProject = Path.Combine(projectRoot, ".claude", "settings.local.json");
            var codexProject  = Path.Combine(projectRoot, ".codex", "hooks.json");

            var claudeOutcome = PluginCommand.RemoveClaudePlugin(claudeProject);

            if (claudeOutcome == PluginCommand.ClaudeRemovalOutcome.Removed) {
                await Console.Out.WriteLineAsync($"Plugin removed (project: {claudeProject})");
            }

            if (File.Exists(codexProject) && PluginCommand.RemoveCodexHooks(codexProject)) {
                await Console.Out.WriteLineAsync($"Codex hooks removed (project: {codexProject})");
            }
        }

        if (!keepConfig && Directory.Exists(configDir)) {
            try {
                Directory.Delete(configDir, recursive: true);
                await Console.Out.WriteLineAsync($"Removed config directory: {configDir}");
            } catch (Exception ex) {
                await Console.Error.WriteLineAsync($"Could not remove config directory {configDir}: {ex.Message}");

                return 1;
            }
        }

        await Console.Out.WriteLineAsync("kapacitor uninstalled.");

        return 0;
    }

    /// <summary>
    /// Resolves the kapacitor config directory the same way
    /// <see cref="PathHelpers"/> would on a fresh process — read
    /// <c>KAPACITOR_CONFIG_DIR</c> first, fall back to
    /// <c>$HOME/.config/kapacitor</c>. Re-evaluates every call so tests that
    /// override <c>HOME</c> see the override even after <c>PathHelpers</c>
    /// has captured a different value into its static cache.
    /// </summary>
    static string ResolveConfigDir() {
        var env = Environment.GetEnvironmentVariable("KAPACITOR_CONFIG_DIR");

        return !string.IsNullOrWhiteSpace(env)
            ? env
            : Path.Combine(PathHelpers.HomeDirectory, ".config", "kapacitor");
    }
}
