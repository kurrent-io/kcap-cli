using Capacitor.Cli.Core;
using Spectre.Console;

namespace Capacitor.Cli.Commands;

/// <summary>
/// Pure logic for the "coding agents" step of <c>kcap setup</c>. All I/O
/// (filesystem, console, prompts) flows through injected delegates so tests
/// can drive every branch without touching ~/.claude, ~/.codex, or AnsiConsole.
/// </summary>
internal static class CodingAgentsStep {
    // New-vendor fields are appended with defaults so existing (named-arg) call
    // sites and the broad CodingAgentsStep test suite compile unchanged. Gemini
    // (AI-887), Kiro (AI-888), and Pi (AI-886) were all added after the original
    // four vendors.
    internal record Options(bool SkipClaude, bool SkipCodex, bool SkipCursor, bool SkipCopilot, bool NoPrompt, bool SkipGemini = false, bool SkipKiro = false, bool SkipPi = false, bool SkipOpenCode = false, bool SkipCodexNetworkAccess = false);

    internal record DetectedAgents(bool Claude, bool Codex, bool Cursor, bool Copilot, bool Gemini = false, bool Kiro = false, bool Pi = false, bool OpenCode = false);

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
            string  KiroHooksPath = "",
            string  PiExtensionPath = "",
            string  OpenCodeExtensionPath = "",
            string  CodexConfigTomlPath = ""
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
            Func<string /*agentJsonPath*/, bool>?                     InstallKiroHooks = null,
            Func<string /*extensionPath*/, bool>?                     InstallPiExtension = null,
            Func<string /*pluginPath*/, bool>?                        InstallOpenCodeExtension = null,
            Func<CodexConfigToml.Change>?                            EnableCodexNetworkAccess = null,
            Func<CodexConfigToml.Change>?                            RegisterCodexMcp = null
        );

    internal record Result(
            bool ClaudeInstalled,
            bool CodexHooksInstalled,
            bool CodexSkillsInstalled,
            bool CursorHooksInstalled,
            bool CopilotHooksInstalled,
            bool GeminiHooksInstalled = false,
            bool KiroHooksInstalled = false,
            bool PiExtensionInstalled = false,
            bool OpenCodeExtensionInstalled = false,
            bool CodexNetworkAccessApplied = false,
            bool CodexMcpRegistered = false
        ) {
        /// <summary>
        /// True when at least one agent's hooks were installed — i.e. there's a
        /// session that will start streaming on the agent's next launch. Skills-only
        /// installs don't count: skills add recall/commands, not the SessionStart hook
        /// that records transcripts. Co-located with the record so the set stays in sync
        /// as agents are added (consumers like SetupCommand's restart tip key off this).
        /// </summary>
        internal bool AnyHooksInstalled =>
            ClaudeInstalled || CodexHooksInstalled || CursorHooksInstalled || CopilotHooksInstalled || GeminiHooksInstalled || KiroHooksInstalled || PiExtensionInstalled || OpenCodeExtensionInstalled;
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
        var codexNetworkApplied   = HandleCodexNetworkAccess(options, paths, installers, prompt, writeLine, codexHooksInstalled);
        var codexMcpRegistered    = HandleCodexMcp(paths, installers, writeLine, codexHooksInstalled);
        var cursorHooksInstalled  = HandleCursorHooks(options, detected, paths, installers, prompt, writeLine);
        var copilotHooksInstalled = HandleCopilotHooks(options, detected, paths, installers, prompt, writeLine);
        var geminiHooksInstalled  = HandleGeminiHooks(options, detected, paths, installers, prompt, writeLine);
        var kiroHooksInstalled    = HandleKiroHooks(options, detected, paths, installers, prompt, writeLine);
        var piExtensionInstalled  = HandlePiExtension(options, detected, paths, installers, prompt, writeLine);
        var openCodeExtensionInstalled = HandleOpenCodeExtension(options, detected, paths, installers, prompt, writeLine);

        if (detected is { Claude: false, Codex: false, Cursor: false, Copilot: false, Gemini: false, Kiro: false, Pi: false, OpenCode: false }) {
            writeLine("  [yellow]⚠ No supported agent CLI detected.[/] Install Claude Code, Codex CLI, Cursor, Copilot CLI, Gemini CLI, Kiro CLI, Pi, or OpenCode to start capturing sessions.");
        }

        return Task.FromResult(
            new Result(
                claudeInstalled,
                codexHooksInstalled,
                codexSkillsInstalled,
                cursorHooksInstalled,
                copilotHooksInstalled,
                geminiHooksInstalled,
                kiroHooksInstalled,
                piExtensionInstalled,
                openCodeExtensionInstalled,
                codexNetworkApplied,
                codexMcpRegistered
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

    static bool HandlePiExtension(
            Options            options,
            DetectedAgents     detected,
            Paths              paths,
            Installers         installers,
            Func<string, bool> prompt,
            Action<string>     writeLine
        ) {
        if (!detected.Pi) {
            writeLine("  [dim]· Pi not detected — skipping[/]");

            return false;
        }

        writeLine("  [green]✓[/] Pi detected");

        if (options.SkipPi) {
            writeLine("  [dim]· Pi extension skipped by flag[/]");

            return false;
        }

        if (installers.InstallPiExtension is null) return false;

        var shouldInstall = options.NoPrompt || prompt("Install the Pi extension (live session capture)?");

        if (!shouldInstall) {
            writeLine("  [dim]· Pi extension not installed (you can run kcap plugin install --pi later)[/]");

            return false;
        }

        // Pi has no shell hooks — the extension (kcap.ts) shells out to the bare
        // "kcap hook --pi" command, so pi must find kcap on PATH (same precheck
        // as the Cursor/Copilot branches).
        if (!installers.CapacitorOnPath()) {
            writeLine("  [yellow]⚠[/] Pi extension not installed — 'kcap' is not on PATH.");
            writeLine("    [dim]Re-install via npm: [/][cyan]npm install -g @kurrent/kcap[/]");

            return false;
        }

        var ok = installers.InstallPiExtension(paths.PiExtensionPath);

        if (!ok) {
            writeLine("  [yellow]⚠[/] Could not write the Pi extension file.");

            return false;
        }

        writeLine($"  [green]✓[/] Pi extension installed ({Markup.Escape(paths.PiExtensionPath)})");
        writeLine("  [dim]  Note: Pi loads extensions at startup — restart any running pi session to pick it up.[/]");

        return true;
    }

    static bool HandleOpenCodeExtension(
            Options            options,
            DetectedAgents     detected,
            Paths              paths,
            Installers         installers,
            Func<string, bool> prompt,
            Action<string>     writeLine
        ) {
        if (!detected.OpenCode) {
            writeLine("  [dim]· OpenCode not detected — skipping[/]");

            return false;
        }

        writeLine("  [green]✓[/] OpenCode detected");

        if (options.SkipOpenCode) {
            writeLine("  [dim]· OpenCode plugin skipped by flag[/]");

            return false;
        }

        if (installers.InstallOpenCodeExtension is null) return false;

        var shouldInstall = options.NoPrompt || prompt("Install the OpenCode plugin (live session capture)?");

        if (!shouldInstall) {
            writeLine("  [dim]· OpenCode plugin not installed (you can run kcap plugin install --opencode later)[/]");

            return false;
        }

        // OpenCode has no shell hooks — the plugin (kcap.ts) shells out to the bare
        // "kcap hook --opencode" command, so OpenCode must find kcap on PATH (same
        // precheck as the Pi/Cursor/Copilot branches).
        if (!installers.CapacitorOnPath()) {
            writeLine("  [yellow]⚠[/] OpenCode plugin not installed — 'kcap' is not on PATH.");
            writeLine("    [dim]Re-install via npm: [/][cyan]npm install -g @kurrent/kcap[/]");

            return false;
        }

        var ok = installers.InstallOpenCodeExtension(paths.OpenCodeExtensionPath);

        if (!ok) {
            writeLine("  [yellow]⚠[/] Could not write the OpenCode plugin file.");

            return false;
        }

        writeLine($"  [green]✓[/] OpenCode plugin installed ({Markup.Escape(paths.OpenCodeExtensionPath)})");
        writeLine("  [dim]  Note: OpenCode loads plugins at startup — restart any running opencode session to pick it up.[/]");

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

    /// <summary>
    /// AI-794 — Codex runs the agent's shell tool in a network-blocked sandbox, so kcap
    /// skills (which shell out to <c>kcap …</c>) can't reach the Capacitor server. After
    /// Codex hooks install, offer to enable sandbox network access for the configured
    /// server(s) via <see cref="Installers.EnableCodexNetworkAccess"/>. Gated on hooks
    /// actually installing — there's nothing to fix if the Codex integration is off.
    /// </summary>
    static bool HandleCodexNetworkAccess(
            Options            options,
            Paths              paths,
            Installers         installers,
            Func<string, bool> prompt,
            Action<string>     writeLine,
            bool               codexHooksInstalled
        ) {
        if (!codexHooksInstalled || installers.EnableCodexNetworkAccess is null) return false;

        if (options.SkipCodexNetworkAccess) {
            writeLine("  [dim]· Codex sandbox network access left unchanged (--skip-codex-network-access)[/]");

            return false;
        }

        writeLine("  [dim]  kcap skills (recap, errors, …) run in Codex's sandbox, which blocks network by default.[/]");

        var shouldApply = options.NoPrompt
            || prompt("Allow Codex's sandbox to reach your Capacitor server(s) so kcap skills work?");

        if (!shouldApply) {
            writeLine("  [dim]· Codex sandbox network access not changed — kcap skills may prompt for escalation (see README)[/]");

            return false;
        }

        var configPath = Markup.Escape(paths.CodexConfigTomlPath);

        switch (installers.EnableCodexNetworkAccess()) {
            case CodexConfigToml.Change.Updated:
                writeLine($"  [green]✓[/] Codex sandbox network access enabled for kcap ([dim]{configPath}[/])");

                return true;
            case CodexConfigToml.Change.Unchanged:
                writeLine("  [dim]· Codex sandbox already allows network access — no change needed[/]");

                return false;
            default:
                writeLine($"  [yellow]⚠[/] Could not update {configPath} — enable Codex sandbox network access manually (see README).");

                return false;
        }
    }

    /// <summary>
    /// Registers the kcap MCP servers (kcap-review, kcap-sessions) in
    /// <c>~/.codex/config.toml</c> via <see cref="Installers.RegisterCodexMcp"/> so Codex
    /// CLI picks them up with no manual TOML edit. Gated on Codex hooks installing — the
    /// same "full Codex integration" trigger as skills. No prompt: registration is
    /// non-destructive (only adds missing kcap servers) and mirrors how the Claude plugin
    /// auto-registers its MCP servers. <c>kcap-flows</c> stays Claude-only (AI-1056).
    /// </summary>
    static bool HandleCodexMcp(
            Paths          paths,
            Installers     installers,
            Action<string> writeLine,
            bool           codexHooksInstalled
        ) {
        if (!codexHooksInstalled || installers.RegisterCodexMcp is null) return false;

        var configPath = Markup.Escape(paths.CodexConfigTomlPath);

        switch (installers.RegisterCodexMcp()) {
            case CodexConfigToml.Change.Updated:
                writeLine($"  [green]✓[/] Codex MCP servers registered: kcap-review, kcap-sessions ([dim]{configPath}[/])");

                return true;
            case CodexConfigToml.Change.Unchanged:
                writeLine("  [dim]· Codex MCP servers already registered — no change needed[/]");

                return false;
            default:
                writeLine($"  [yellow]⚠[/] Could not register Codex MCP servers in {configPath} — see README to add them manually.");

                return false;
        }
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
