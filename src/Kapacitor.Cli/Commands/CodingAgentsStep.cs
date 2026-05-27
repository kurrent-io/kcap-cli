using Spectre.Console;

namespace Kapacitor.Cli.Commands;

/// <summary>
/// Pure logic for the "coding agents" step of <c>kapacitor setup</c>. All I/O
/// (filesystem, console, prompts) flows through injected delegates so tests
/// can drive every branch without touching ~/.claude, ~/.codex, or AnsiConsole.
/// </summary>
internal static class CodingAgentsStep {
    internal record Options(bool SkipClaude, bool SkipCodex, bool NoPrompt);
    internal record DetectedAgents(bool Claude, bool Codex);
    internal record Paths(
        string ClaudeSettingsPath,
        string ClaudeScopeLabel,
        string? PluginDir,
        string CodexHooksPath,
        string AgentsSkillsDir,
        string LegacyCodexSkillsDir);
    internal record Installers(
        Func<string /*settingsPath*/, string /*pluginDir*/, bool> InstallClaudePlugin,
        Func<string /*hooksPath*/, bool>                          InstallCodexHooks,
        Func<string /*srcDir*/, string /*dstDir*/, bool>          InstallAgentSkills,
        Func<string /*legacyDir*/, bool>                          CleanLegacyCodexSkills);
    internal record Result(bool ClaudeInstalled, bool CodexHooksInstalled, bool CodexSkillsInstalled);

    /// <summary>
    /// Drives the agent-detection branches and dispatches to the installer
    /// delegates. Subsequent tasks fill in Claude, Codex hooks, Codex skills,
    /// and neither-detected behaviour.
    /// </summary>
    internal static Task<Result> RunAsync(
        Options options,
        DetectedAgents detected,
        Paths paths,
        Installers installers,
        Func<string, bool> prompt,
        Action<string> writeLine) {
        var claudeInstalled      = HandleClaude(options, detected, paths, installers, prompt, writeLine);
        var codexHooksInstalled  = HandleCodexHooks(options, detected, paths, installers, prompt, writeLine);
        var codexSkillsInstalled = codexHooksInstalled
            ? HandleCodexSkills(paths, installers, writeLine)
            : false;

        if (!detected.Claude && !detected.Codex) {
            writeLine("  [yellow]⚠ No supported agent CLI detected.[/] Install Claude Code or Codex CLI to start capturing sessions.");
        }

        return Task.FromResult(new Result(claudeInstalled, codexHooksInstalled, codexSkillsInstalled));
    }

    static bool HandleCodexSkills(
        Paths paths,
        Installers installers,
        Action<string> writeLine) {
        if (paths.PluginDir is null) {
            writeLine("  [yellow]⚠[/] Codex hooks installed but agent skills could not be copied (plugin directory not found).");
            return false;
        }

        var src = Path.Combine(paths.PluginDir, "skills");
        var ok  = installers.InstallAgentSkills(src, paths.AgentsSkillsDir);

        if (!ok) {
            writeLine($"  [yellow]⚠[/] Codex hooks installed but agent skills could not be copied to {Markup.Escape(paths.AgentsSkillsDir)}");
            return false;
        }

        writeLine($"  [green]✓[/] Agent skills installed (user: {Markup.Escape(paths.AgentsSkillsDir)})");
        writeLine("    [dim]kapacitor-recap, kapacitor-errors, kapacitor-hide, kapacitor-disable, kapacitor-validate-plan[/]");
        installers.CleanLegacyCodexSkills(paths.LegacyCodexSkillsDir);
        return true;
    }

    static bool HandleCodexHooks(
        Options options,
        DetectedAgents detected,
        Paths paths,
        Installers installers,
        Func<string, bool> prompt,
        Action<string> writeLine) {
        if (!detected.Codex) {
            writeLine("  [dim]· Codex CLI not found on PATH — skipping[/]");
            return false;
        }

        writeLine("  [green]✓[/] Codex CLI detected");

        if (options.SkipCodex) {
            writeLine("  [dim]· Codex CLI hooks skipped by flag[/]");
            return false;
        }

        var shouldInstall = options.NoPrompt || prompt("Install Codex CLI hooks and kapacitor agent skills?");

        if (!shouldInstall) {
            writeLine("  [dim]· Codex CLI hooks not installed (you can run kapacitor plugin install --codex later)[/]");
            return false;
        }

        var ok = installers.InstallCodexHooks(paths.CodexHooksPath);

        if (!ok) {
            writeLine("  [yellow]⚠[/] Could not write Codex hooks file.");
            return false;
        }

        writeLine($"  [green]✓[/] Codex hooks installed (user: {Markup.Escape(paths.CodexHooksPath)})");
        writeLine("  [dim]  Next: run /hooks inside Codex and trust each kapacitor entry —[/]");
        writeLine("  [dim]  Codex won't execute hooks until each is explicitly trusted.[/]");
        return true;
    }

    static bool HandleClaude(
        Options options,
        DetectedAgents detected,
        Paths paths,
        Installers installers,
        Func<string, bool> prompt,
        Action<string> writeLine) {
        if (!detected.Claude) {
            writeLine("  [dim]· Claude Code not found on PATH — skipping[/]");
            return false;
        }

        writeLine("  [green]✓[/] Claude Code detected");

        if (options.SkipClaude) {
            writeLine("  [dim]· Claude Code plugin skipped by flag[/]");
            return false;
        }

        var shouldInstall = options.NoPrompt || prompt("Install Claude Code plugin (hooks, skills, memory)?");

        if (!shouldInstall) {
            writeLine("  [dim]· Claude Code plugin not installed (you can run kapacitor plugin install later)[/]");
            return false;
        }

        if (paths.PluginDir is null) {
            writeLine("  [yellow]⚠[/] Plugin directory not found. Re-install kapacitor via npm:");
            writeLine("    [cyan]npm install -g @kurrent/kapacitor[/]");
            return false;
        }

        var ok = installers.InstallClaudePlugin(paths.ClaudeSettingsPath, paths.PluginDir);

        if (!ok) {
            writeLine($"  [yellow]⚠[/] Could not update Claude settings file. Install manually inside Claude Code: [cyan]/plugin install {Markup.Escape(paths.PluginDir)}[/]");
            return false;
        }

        writeLine($"  [green]✓[/] Claude Code plugin installed ({Markup.Escape(paths.ClaudeScopeLabel)}: {Markup.Escape(paths.ClaudeSettingsPath)})");
        return true;
    }
}
