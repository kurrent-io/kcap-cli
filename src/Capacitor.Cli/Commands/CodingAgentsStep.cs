using Spectre.Console;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Pure logic for the "coding agents" step of <c>kcap setup</c>. All I/O
/// (filesystem, console, prompts) flows through injected delegates so tests
/// can drive every branch without touching ~/.claude, ~/.codex, or AnsiConsole.
/// </summary>
internal static class CodingAgentsStep {
    // New-vendor fields are appended with defaults so existing (named-arg) call
    // sites and the broad test suite compile unchanged. Gemini (AI-887) and Kiro
    // (AI-888) were both added after the original four vendors.
    internal record Options(bool SkipClaude, bool SkipCodex, bool SkipCursor, bool SkipCopilot, bool NoPrompt, bool SkipGemini = false, bool SkipKiro = false);

    internal record DetectedAgents(bool Claude, bool Codex, bool Cursor, bool Copilot, bool Gemini = false, bool Kiro = false);

    internal record Paths(
            string  ClaudeSettingsPath,
            string  ClaudeScopeLabel,
            string? PluginDir,
            string  CodexHooksPath,
            string  CursorHooksPath,
            string  CopilotHooksPath,
            string  GeminiSettingsPath,
            string  AgentsSkillsDir,
            string  LegacyCodexSkillsDir,
            string  KiroHooksPath = ""
        );

    internal record Installers(
            Func<string /*settingsPath*/, string /*pluginDir*/, bool> InstallClaudePlugin,
            Func<string /*hooksPath*/, bool>                          InstallCodexHooks,
            Func<string /*hooksPath*/, bool>                          InstallCursorHooks,
            Func<string /*hooksPath*/, bool>                          InstallCopilotHooks,
            Func<string /*settingsPath*/, bool>                       InstallGeminiHooks,
            Func<bool>                                                CapacitorOnPath,
            Func<string /*srcDir*/, string /*dstDir*/, bool>          InstallAgentSkills,
            Func<string /*legacyDir*/, bool>                          CleanLegacyCodexSkills,
            Func<string /*agentJsonPath*/, bool>?                     InstallKiroHooks = null
        );

    internal record Result(
            bool ClaudeInstalled,
            bool CodexHooksInstalled,
            bool CodexSkillsInstalled,
            bool CursorHooksInstalled,
            bool CopilotHooksInstalled,
            bool GeminiHooksInstalled = false,
            bool KiroHooksInstalled = false
        ) {
        /// <summary>
        /// True when at least one agent's hooks were installed — i.e. there's a
        /// session that will start streaming on the agent's next launch. Skills-only
        /// installs don't count: skills add recall/commands, not the SessionStart hook
        /// that records transcripts. Co-located with the record so the set stays in sync
        /// as agents are added (consumers like SetupCommand's restart tip key off this).
        /// </summary>
        internal bool AnyHooksInstalled =>
            ClaudeInstalled || CodexHooksInstalled || CursorHooksInstalled || CopilotHooksInstalled || GeminiHooksInstalled || KiroHooksInstalled;
    }

    /// <summary>
    /// Drives the agent-detection branches and dispatches to the installer
    /// delegates. Subsequent tasks fill in Claude, Codex hooks, Codex skills,
    /// Cursor hooks, and neither-detected behaviour.
    /// </summary>
    internal static Task<Result> RunAsync(
            Options            options,
            DetectedAgents     detected,
            Paths              paths,
            Installers         installers,
            Func<string, bool> prompt,
            Action<string>     writeLine
        ) {
        var claudeInstalled       = HandleClaude(options, detected, paths, installers, prompt, writeLine);
        var codexHooksInstalled   = HandleCodexHooks(options, detected, paths, installers, prompt, writeLine);
        var codexSkillsInstalled  = codexHooksInstalled && HandleCodexSkills(paths, installers, writeLine);
        var cursorHooksInstalled  = HandleCursorHooks(options, detected, paths, installers, prompt, writeLine);
        var copilotHooksInstalled = HandleCopilotHooks(options, detected, paths, installers, prompt, writeLine);
        var geminiHooksInstalled  = HandleGeminiHooks(options, detected, paths, installers, prompt, writeLine);
        var kiroHooksInstalled    = HandleKiroHooks(options, detected, paths, installers, prompt, writeLine);

        if (detected is { Claude: false, Codex: false, Cursor: false, Copilot: false, Gemini: false, Kiro: false }) {
            writeLine("  [yellow]⚠ No supported agent CLI detected.[/] Install Claude Code, Codex CLI, Cursor, Copilot CLI, Gemini CLI, or Kiro CLI to start capturing sessions.");
        }

        return Task.FromResult(
            new Result(
                claudeInstalled,
                codexHooksInstalled,
                codexSkillsInstalled,
                cursorHooksInstalled,
                copilotHooksInstalled,
                geminiHooksInstalled,
                kiroHooksInstalled
            )
        );
    }

    static bool HandleKiroHooks(
            Options            options,
            DetectedAgents     detected,
            Paths              paths,
            Installers         installers,
            Func<string, bool> prompt,
            Action<string>     writeLine
        ) {
        if (!detected.Kiro) {
            writeLine("  [dim]· Kiro CLI not detected — skipping[/]");

            return false;
        }

        writeLine("  [green]✓[/] Kiro CLI detected");

        if (options.SkipKiro) {
            writeLine("  [dim]· Kiro CLI hooks skipped by flag[/]");

            return false;
        }

        var shouldInstall = options.NoPrompt || prompt("Install Kiro CLI hooks?");

        if (!shouldInstall) {
            writeLine("  [dim]· Kiro hooks not installed (you can run kcap plugin install --kiro later)[/]");

            return false;
        }

        // The agent JSON writes the bare "kcap hook --kiro" command and relies on
        // Kiro finding kcap on PATH — same precheck as the Cursor/Copilot branches.
        if (!installers.CapacitorOnPath()) {
            writeLine("  [yellow]⚠[/] Kiro hooks not installed — 'kcap' is not on PATH.");
            writeLine("    [dim]Re-install via npm: [/][cyan]npm install -g @kurrent/kcap[/]");

            return false;
        }

        var ok = installers.InstallKiroHooks?.Invoke(paths.KiroHooksPath) ?? false;

        if (!ok) {
            writeLine("  [yellow]⚠[/] Could not write Kiro agent hooks file.");

            return false;
        }

        writeLine($"  [green]✓[/] Kiro hooks installed ({Markup.Escape(paths.KiroHooksPath)})");
        writeLine("  [dim]  Note: Kiro loads agent hooks at startup — restart any running kiro session to pick them up.[/]");

        return true;
    }

    static bool HandleCopilotHooks(
            Options            options,
            DetectedAgents     detected,
            Paths              paths,
            Installers         installers,
            Func<string, bool> prompt,
            Action<string>     writeLine
        ) {
        if (!detected.Copilot) {
            writeLine("  [dim]· Copilot CLI not detected — skipping[/]");

            return false;
        }

        writeLine("  [green]✓[/] Copilot CLI detected");

        if (options.SkipCopilot) {
            writeLine("  [dim]· Copilot CLI hooks skipped by flag[/]");

            return false;
        }

        var shouldInstall = options.NoPrompt || prompt("Install Copilot CLI hooks?");

        if (!shouldInstall) {
            writeLine("  [dim]· Copilot hooks not installed (you can run kcap plugin install --copilot later)[/]");

            return false;
        }

        // kcap.json writes the bare "kcap hook --copilot" command and relies
        // on Copilot finding it on PATH — same precheck as the Cursor branch.
        if (!installers.CapacitorOnPath()) {
            writeLine("  [yellow]⚠[/] Copilot hooks not installed — 'kcap' is not on PATH.");
            writeLine("    [dim]Re-install via npm: [/][cyan]npm install -g @kurrent/kcap[/]");

            return false;
        }

        var ok = installers.InstallCopilotHooks(paths.CopilotHooksPath);

        if (!ok) {
            writeLine("  [yellow]⚠[/] Could not write Copilot hooks file.");

            return false;
        }

        writeLine($"  [green]✓[/] Copilot hooks installed ({Markup.Escape(paths.CopilotHooksPath)})");
        writeLine("  [dim]  Note: Copilot loads hook config at startup — restart any running copilot session to pick them up.[/]");

        return true;
    }

    static bool HandleGeminiHooks(
            Options            options,
            DetectedAgents     detected,
            Paths              paths,
            Installers         installers,
            Func<string, bool> prompt,
            Action<string>     writeLine
        ) {
        if (!detected.Gemini) {
            writeLine("  [dim]· Gemini CLI not detected — skipping[/]");

            return false;
        }

        writeLine("  [green]✓[/] Gemini CLI detected");

        if (options.SkipGemini) {
            writeLine("  [dim]· Gemini CLI hooks skipped by flag[/]");

            return false;
        }

        var shouldInstall = options.NoPrompt || prompt("Install Gemini CLI hooks?");

        if (!shouldInstall) {
            writeLine("  [dim]· Gemini hooks not installed (you can run kcap plugin install --gemini later)[/]");

            return false;
        }

        // settings.json writes the bare "kcap hook --gemini" command and relies
        // on Gemini finding it on PATH — same precheck as the Cursor/Copilot branch.
        if (!installers.CapacitorOnPath()) {
            writeLine("  [yellow]⚠[/] Gemini hooks not installed — 'kcap' is not on PATH.");
            writeLine("    [dim]Re-install via npm: [/][cyan]npm install -g @kurrent/kcap[/]");

            return false;
        }

        var ok = installers.InstallGeminiHooks(paths.GeminiSettingsPath);

        if (!ok) {
            writeLine("  [yellow]⚠[/] Could not install Gemini hooks — ensure settings.json is valid JSON (left untouched).");

            return false;
        }

        writeLine($"  [green]✓[/] Gemini hooks installed ({Markup.Escape(paths.GeminiSettingsPath)})");
        writeLine("  [dim]  Note: Gemini loads hook config at startup — restart any running gemini session to pick them up.[/]");

        return true;
    }

    static bool HandleCodexSkills(
            Paths          paths,
            Installers     installers,
            Action<string> writeLine
        ) {
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
        writeLine("    [dim]kcap-recap, kcap-errors, kcap-hide, kcap-disable, kcap-validate-plan[/]");
        installers.CleanLegacyCodexSkills(paths.LegacyCodexSkillsDir);

        return true;
    }

    static bool HandleCodexHooks(
            Options            options,
            DetectedAgents     detected,
            Paths              paths,
            Installers         installers,
            Func<string, bool> prompt,
            Action<string>     writeLine
        ) {
        if (!detected.Codex) {
            writeLine("  [dim]· Codex CLI not found on PATH — skipping[/]");

            return false;
        }

        writeLine("  [green]✓[/] Codex CLI detected");

        if (options.SkipCodex) {
            writeLine("  [dim]· Codex CLI hooks skipped by flag[/]");

            return false;
        }

        var shouldInstall = options.NoPrompt || prompt("Install Codex CLI hooks and kcap agent skills?");

        if (!shouldInstall) {
            writeLine("  [dim]· Codex CLI hooks not installed (you can run kcap plugin install --codex later)[/]");

            return false;
        }

        var ok = installers.InstallCodexHooks(paths.CodexHooksPath);

        if (!ok) {
            writeLine("  [yellow]⚠[/] Could not write Codex hooks file.");

            return false;
        }

        writeLine($"  [green]✓[/] Codex hooks installed (user: {Markup.Escape(paths.CodexHooksPath)})");
        writeLine("  [dim]  Next: Codex will prompt to trust the kcap hooks on its next launch —[/]");
        writeLine("  [dim]  accept once to trust them all (or run /hooks inside Codex to trust them individually).[/]");

        return true;
    }

    static bool HandleCursorHooks(
            Options            options,
            DetectedAgents     detected,
            Paths              paths,
            Installers         installers,
            Func<string, bool> prompt,
            Action<string>     writeLine
        ) {
        if (!detected.Cursor) {
            writeLine("  [dim]· Cursor not detected — skipping[/]");

            return false;
        }

        writeLine("  [green]✓[/] Cursor detected");

        if (options.SkipCursor) {
            writeLine("  [dim]· Cursor hooks skipped by flag[/]");

            return false;
        }

        var shouldInstall = options.NoPrompt || prompt("Install Cursor IDE hooks?");

        if (!shouldInstall) {
            writeLine("  [dim]· Cursor hooks not installed (you can run kcap plugin install --cursor later)[/]");

            return false;
        }

        // hooks.json writes the bare "kcap hook --cursor" command and
        // relies on Cursor finding it on PATH. If kcap isn't on PATH
        // we'd write a config Cursor can't execute — surface a setup error
        // instead. Mirror of PluginCommand.InstallCursor's precheck.
        if (!installers.CapacitorOnPath()) {
            writeLine("  [yellow]⚠[/] Cursor hooks not installed — 'kcap' is not on PATH.");
            writeLine("    [dim]Re-install via npm: [/][cyan]npm install -g @kurrent/kcap[/]");

            return false;
        }

        var ok = installers.InstallCursorHooks(paths.CursorHooksPath);

        if (!ok) {
            writeLine("  [yellow]⚠[/] Could not write Cursor hooks file.");

            return false;
        }

        writeLine($"  [green]✓[/] Cursor hooks installed ({Markup.Escape(paths.CursorHooksPath)})");

        return true;
    }

    static bool HandleClaude(
            Options            options,
            DetectedAgents     detected,
            Paths              paths,
            Installers         installers,
            Func<string, bool> prompt,
            Action<string>     writeLine
        ) {
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
            writeLine("  [dim]· Claude Code plugin not installed (you can run kcap plugin install later)[/]");

            return false;
        }

        if (paths.PluginDir is null) {
            writeLine("  [yellow]⚠[/] Plugin directory not found. Re-install kcap via npm:");
            writeLine("    [cyan]npm install -g @kurrent/kcap[/]");

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
