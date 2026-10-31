using System.Collections.Frozen;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Antigravity;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Harness.Copilot;
using Capacitor.Cli.Core.Harness.Cursor;
using Capacitor.Cli.Core.Harness.Gemini;
using Capacitor.Cli.Core.Harness.Grok;
using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.Core.Harness.MistralVibe;
using Capacitor.Cli.Core.Harness.Muse;
using Capacitor.Cli.Core.Harness.OpenCode;
using Capacitor.Cli.Core.Harness.Pi;
using Capacitor.Cli.Core.Toml;
using Capacitor.Cli.Core.Instructions;
using Capacitor.Cli.Core.Mcp;

namespace Capacitor.Cli.Commands;

public sealed class PluginCommand(PluginEnvironment env, WorkingDirectory workdir) {
    static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    const string CursorHookCommand  = "kcap hook --cursor";
    const string CopilotHookCommand = "kcap hook --copilot";
    const string KiroHookCommand    = "kcap hook --kiro";

    const int DefaultHookTimeout = 30;

    bool _refreshFailed;

    /// <summary>Whether the bare <c>kcap</c> those commands invoke resolves on the same search path
    /// the vendors were found on — a hook writing a command this cannot find never fires.</summary>
    bool KcapOnPath => env.Binaries.Finds("kcap");

    public async Task<int> HandleAsync(string[] args) {
        if (args.Length < 2) {
            PrintUsage();

            return 1;
        }

        _refreshFailed = false;
        var exit = args[1] switch {
            "install" => await Install(args),
            "remove"  => await Remove(args),
            _         => PrintUsage()
        };
        return args.Contains("--strict-refresh") && _refreshFailed ? 1 : exit;
    }

    static readonly string[] ExclusiveTargetFlags = ["--codex", "--cursor", "--copilot", "--gemini", "--kiro", "--pi", "--opencode", "--antigravity", "--mistral-vibe", "--grok", "--muse", "--skills"];

    const string MutuallyExclusiveMsg =
        "--cursor, --codex, --copilot, --gemini, --kiro, --pi, --opencode, --antigravity, --mistral-vibe, --grok, --muse, and --skills are mutually exclusive.";

    static bool HasConflictingTargets(string[] args) =>
        ExclusiveTargetFlags.Count(args.Contains) > 1;

    async Task<int> Install(string[] args) {
        if (HasConflictingTargets(args)) {
            await env.Stderr.WriteLineAsync(MutuallyExclusiveMsg);

            return 1;
        }

        if (args.Contains("--skills")) return await InstallSkills(args);

        var exit = await InstallHarness(args);

        if (exit == 0 && !args.Contains(ToolsOnlyFlag)) {
            var gitHook = new GitHookInstaller(env.Home, env.ResolveMcpBinaryPath);

            // Refresh may repair a recording hook, but cannot opt into one.
            if (args.Contains("--if-installed")) {
                if (gitHook.IsInstalled() && !gitHook.Refresh()) {
                    _refreshFailed = true;
                    await env.Stderr.WriteLineAsync("Could not refresh the installed git hook.");
                }
            }
            else gitHook.Install();
        }

        return exit;
    }

    const string ToolsOnlyFlag = "--tools-only";

    Task<int> InstallHarness(string[] args) {
        if (args.Contains(ToolsOnlyFlag)) {
            if (args.Contains("--codex"))
                return RefuseToolsOnlyAsync("Codex", "its hooks and skills install as one unit");

            if (args.Contains("--grok"))
                return RefuseToolsOnlyAsync("Grok", "kcap installs no tools for it; Grok loads the ones set up for Claude Code");

            if (!ExclusiveTargetFlags.Any(args.Contains))
                return RefuseToolsOnlyAsync("Claude Code", "its MCP servers and skills ship inside the plugin that records sessions");
        }

        if (args.Contains("--codex")) return InstallCodex(args);
        if (args.Contains("--cursor")) return InstallCursor(args);
        if (args.Contains("--copilot")) return InstallCopilot(args);
        if (args.Contains("--gemini")) return InstallGemini(args);
        if (args.Contains("--kiro")) return InstallKiro(args);
        if (args.Contains("--pi")) return InstallPi(args);
        if (args.Contains("--opencode")) return InstallOpenCode(args);
        if (args.Contains("--antigravity")) return InstallAntigravity(args);
        if (args.Contains("--mistral-vibe")) return InstallVibe(args);
        if (args.Contains("--grok")) return InstallGrok(args);
        if (args.Contains("--muse")) return InstallMuse(args);

        return InstallClaude(args);
    }

    async Task<int> RefuseToolsOnlyAsync(string vendor, string reason) {
        await env.Stderr.WriteLineAsync(
            $"--tools-only is not supported for {vendor}: {reason}. "
          + "It applies to --cursor, --copilot, --gemini, --kiro, --pi, --opencode, --antigravity, --mistral-vibe and --muse."
        );

        return 1;
    }

    /// <summary>Whether to write only the tools: asked for, or a refresh of an install whose capture half
    /// is absent — a refresh never adds capture to an install that has only the tools.</summary>
    static bool ToolsOnly(string[] args, bool refreshOnly, bool captureInstalled) =>
        args.Contains(ToolsOnlyFlag) || refreshOnly && !captureInstalled;

    Task NoteCaptureSkippedAsync(string vendor) =>
        env.Stdout.WriteLineAsync($"{vendor} capture unchanged (--tools-only): no recording hooks were added or removed.");

    async Task<int> Remove(string[] args) {
        if (HasConflictingTargets(args)) {
            await env.Stderr.WriteLineAsync(MutuallyExclusiveMsg);

            return 1;
        }

        if (args.Contains("--skills")) return await RemoveSkills(args);
        if (args.Contains("--codex")) return await RemoveCodex(args);
        if (args.Contains("--cursor")) return await RemoveCursor(args);
        if (args.Contains("--copilot")) return await RemoveCopilot(args);
        if (args.Contains("--gemini")) return await RemoveGemini(args);
        if (args.Contains("--kiro")) return await RemoveKiro(args);
        if (args.Contains("--pi")) return await RemovePi(args);
        if (args.Contains("--opencode")) return await RemoveOpenCode(args);
        if (args.Contains("--antigravity")) return await RemoveAntigravity(args);
        if (args.Contains("--mistral-vibe")) return await RemoveVibe(args);
        if (args.Contains("--grok")) return await RemoveGrok(args);
        if (args.Contains("--muse")) return await RemoveMuse(args);

        return await RemoveClaude(args);
    }

    async Task<int> InstallClaude(string[] args) {
        var scope = args.Contains("--project") ? "project" : "user";

        var settingsPath = scope == "project"
            ? Path.Combine(workdir.Path, ".claude", "settings.local.json")
            : env.Harnesses.Of<ClaudeHarness>().Paths.UserSettings;

        // --if-installed: refresh-only mode used by the npm postinstall hook. It never adopts or
        // rewrites a default layout the user never opted into. Paths can change at the same version.
        var refreshOnly      = args.Contains("--if-installed");
        var defaultInstalled = ClaudePluginInstaller.IsInstalled(settingsPath);

        var skipDefault = refreshOnly && !defaultInstalled;

        var exit = skipDefault ? 0 : await InstallClaudeLayout(settingsPath, scope, refreshOnly);

        if (exit != 0 || scope != "user") return exit;

        var defaultHome   = env.Harnesses.Of<ClaudeHarness>().Paths.Home;
        var accountsWired = await WireUserAccountsAsync(
            HarnessId.Claude, defaultHome,
            adoptDefault: !refreshOnly || defaultInstalled, refreshOnly, enableNetwork: false);

        // After adoption, which is what registers the default directory.
        if (!skipDefault) accountsWired &= await DefaultStatusLineAsync(defaultHome, install: true);

        return accountsWired || refreshOnly ? 0 : 1;
    }

    async Task<int> InstallClaudeLayout(string settingsPath, string scope, bool refreshOnly) {
        var pluginPath = env.ResolvePluginPath();

        if (pluginPath is null) {
            if (refreshOnly) { _refreshFailed = true; return 0; }

            await env.Stderr.WriteLineAsync("Plugin directory not found. " + InstallProvenance.ReinstallHint());

            return 1;
        }

        var installed = SetupCommand.InstallPlugin(settingsPath, pluginPath);

        if (installed) {
            await env.Stdout.WriteLineAsync(
                refreshOnly
                    ? $"Plugin refreshed ({scope}: {settingsPath})"
                    : $"Plugin installed ({scope}: {settingsPath})"
            );

            // Claude Code only loads hooks at session start. A fresh install from
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
            if (refreshOnly) { _refreshFailed = true; return 0; }

            await env.Stderr.WriteLineAsync("Could not update settings file.");

            return 1;
        }

        return 0;
    }

    async Task<int> RemoveClaude(string[] args) {
        var scope = args.Contains("--project") ? "project" : "user";

        var settingsPath = scope == "project"
            ? Path.Combine(workdir.Path, ".claude", "settings.local.json")
            : env.Harnesses.Of<ClaudeHarness>().Paths.UserSettings;

        var defaultHome = env.Harnesses.Of<ClaudeHarness>().Paths.Home;

        // Before the layout goes, while the default directory is still registered.
        var statusLineRestored = scope != "user" || await DefaultStatusLineAsync(defaultHome, install: false);

        var exit = await RemoveClaudeLayout(settingsPath, scope);

        if (scope == "user" && !await UnwireUserAccountsAsync(HarnessId.Claude, defaultHome))
            exit = 1;

        if (!statusLineRestored) exit = 1;

        return exit;
    }

    /// <summary>The registered default directory's status line, which <see cref="UserAccountsAsync"/> leaves out.
    /// False only for a fatal failure; an unusable registry is the usual warning.</summary>
    async Task<bool> DefaultStatusLineAsync(string defaultDirectory, bool install) {
        if (env.Accounts is not { } store) return true;

        WiringStep? step;
        try {
            step = await LockedAsync(() =>
                store.Load().Accounts.FirstOrDefault(a => a.Vendor == HarnessId.Claude && AccountDirectory.Same(a.Directory, defaultDirectory)) is { } account
                    ? install ? AccountWiring.WireStatusLine(account.Id, env.Home, store) : AccountWiring.UnwireStatusLine(account.Id, env.Home, store)
                    : new WiringStep("statusline", true, "default directory not registered"));
        } catch (Exception ex) when (IsRegistryFailure(ex)) {
            await RegistryUnavailableAsync(ex);

            return true;
        }

        if (step is null || step.Succeeded) return true;

        var fatal = AccountWiring.HasFatalFailure([step]);
        _refreshFailed |= fatal;
        await env.Stderr.WriteLineAsync(fatal
            ? $"Could not {(install ? "wire" : "restore")} the Claude status line in {defaultDirectory}: {step.Name}: {step.Detail}"
            : $"Warning: the Claude status line in {defaultDirectory}: {step.Name}: {step.Detail}");

        return !fatal;
    }

    async Task<int> RemoveClaudeLayout(string settingsPath, string scope) {
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
    public static ClaudeRemovalOutcome RemoveClaudePlugin(string settingsPath) =>
        ClaudePluginWriter.Remove(settingsPath) switch {
            SettingsEdit.Changed   => ClaudeRemovalOutcome.Removed,
            SettingsEdit.Malformed => ClaudeRemovalOutcome.Malformed,
            SettingsEdit.Failed    => throw new IOException($"Could not write {settingsPath}."),
            _                      => ClaudeRemovalOutcome.NotInstalled,
        };

    public enum ClaudeRemovalOutcome {
        Removed,
        NotInstalled,
        Malformed
    }

    async Task<int> InstallSkills(string[] args) {
        // --if-installed: only refresh when a marker file shows the user has
        // previously installed skills. Used by the npm postinstall hook to
        // keep existing installs up to date without forcing skills onto users
        // who haven't run `kcap setup` yet.
        var refreshOnly = args.Contains("--if-installed");

        switch (refreshOnly) {
            case true when !AgentsSkillsInstaller.IsInstalled(env.Agents.UserSkillsDir):
            // Fast path: marker already matches the current build, no point
            // re-copying every skill on a same-version reinstall (e.g. `npm
            // install -g` of the version already on disk).
            case true when
                AgentsSkillsInstaller.ReadMarker(env.Agents.UserSkillsDir) ==
                AgentsSkillsInstaller.CurrentVersion():
                return 0;
        }

        var pluginPath = env.ResolvePluginPath();

        if (pluginPath is null) {
            if (refreshOnly) { _refreshFailed = true; return 0; }

            await env.Stderr.WriteLineAsync(
                "Cannot install agent skills: kcap plugin folder not found. " +
                InstallProvenance.ReinstallHint()
            );

            return 1;
        }

        var skillsSource = Path.Combine(pluginPath, "skills");

        if (!Directory.Exists(skillsSource)) {
            if (refreshOnly) { _refreshFailed = true; return 0; }

            await env.Stderr.WriteLineAsync(
                $"Cannot install agent skills: 'skills' folder missing from {pluginPath}. " +
                InstallProvenance.ReinstallHint()
            );

            return 1;
        }

        var consumers = refreshOnly ? null : SharedSkillsConsumers.Read(env.Agents.UserSkillsDir, ConfiguredSkillsConsumers);

        if (!AgentsSkillsInstaller.Install(skillsSource, env.Agents.UserSkillsDir)) {
            if (refreshOnly) { _refreshFailed = true; return 0; }

            await env.Stderr.WriteLineAsync("Could not install agent skills.");

            return 1;
        }

        if (consumers is not null)
            SharedSkillsConsumers.Write(env.Agents.UserSkillsDir, consumers.Append(SharedSkillsConsumers.Explicit));

        await env.Stdout.WriteLineAsync(
            refreshOnly
                ? $"Agent skills refreshed (user: {env.Agents.UserSkillsDir})"
                : $"Agent skills installed (user: {env.Agents.UserSkillsDir})"
        );

        AgentsSkillsInstaller.CleanLegacyCodexSkills(env.Harnesses.Of<CodexHarness>().Paths.SkillsDir);

        return 0;
    }

    /// <summary>The explicit removal: every consumer is forgotten, so the integrations still configured
    /// lose the skills too, and the output names them.</summary>
    async Task<int> RemoveSkills(string[] _) {
        var others = (SharedSkillsConsumers.ReadRecorded(env.Agents.UserSkillsDir) ?? FrozenSet<string>.Empty)
            .Where(c => c != SharedSkillsConsumers.Explicit)
            .ToList();
        var agents = SharedSkillsConsumers.Clear(env.Agents.UserSkillsDir);

        await ReportSharedSkillsRemovalAsync(agents);

        if (agents.RemovedAny && others.Count > 0)
            await env.Stdout.WriteLineAsync(
                $"{ConsumerNames(others)} also used them; reinstall that integration to put them back.");

        var legacy = AgentsSkillsInstaller.CleanLegacyCodexSkills(env.Harnesses.Of<CodexHarness>().Paths.SkillsDir);

        if (agents.HadErrors || legacy.HadErrors) {
            await env.Stdout.WriteLineAsync("Removal incomplete — see errors above.");

            return 0;
        }

        if (!agents.RemovedAny && !legacy.RemovedAny && agents.Preserved.Count == 0) {
            await env.Stdout.WriteLineAsync("Nothing to remove — agent skills were not installed.");
        }

        return 0;
    }

    async Task<int> InstallCodex(string[] args) {
        var codex = env.Harnesses.Of<CodexHarness>().Paths;

        var scope = args.Contains("--project") ? "project" : "user";

        var hooksPath = scope == "project"
            ? Path.Combine(workdir.Path, ".codex", "hooks.json")
            : codex.UserHooksJson;

        // --if-installed: refresh-only mode used by the npm postinstall hook and
        // `kcap update`. Skip when the user never opted in. Skills are NOT touched
        // here — `--skills --if-installed` is its own postinstall call.
        var refreshOnly = args.Contains("--if-installed");

        if (refreshOnly) {
            var defaultInstalled = CodexHooksInstaller.IsInstalled(hooksPath);

            // The marker gates only the hooks write. The MCP registration is healed on every
            // refresh: the marker says nothing about config.toml, and a server that joined the
            // Codex set after the last full install is otherwise never registered.
            // Never fail the npm install path.
            if (defaultInstalled) {
                // A byte-order mark makes Codex refuse the file whatever the marker says.
                if (CodexHooksInstaller.ReadMarker(hooksPath) != CapacitorVersion.Current()
                 || CodexHooksInstaller.StartsWithByteOrderMark(hooksPath)) {
                    if (InstallCodexHooks(hooksPath))
                        await env.Stdout.WriteLineAsync($"Codex hooks refreshed ({scope}: {hooksPath})");
                    else {
                        _refreshFailed = true;
                        await env.Stderr.WriteLineAsync(
                            $"Warning: could not refresh Codex hooks ({hooksPath}); continuing with MCP registration.");
                    }
                }

            }

            try {
                if (defaultInstalled || CodexConfigToml.HasMcpInstallRecord(codex.ConfigToml))
                    await RegisterCodexMcpServersAsync();
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) {
                _refreshFailed = true;
                await env.Stderr.WriteLineAsync($"Warning: could not refresh Codex MCP servers in {codex.ConfigToml}: {ex.Message}");
            }

            if (scope == "user")
                await WireUserAccountsAsync(HarnessId.Codex, codex.Home, adoptDefault: defaultInstalled, refreshOnly: true, enableNetwork: false);

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
                InstallProvenance.ReinstallHint()
            );

            return 1;
        }

        var skillsSource = Path.Combine(pluginPath, "skills");

        if (!Directory.Exists(skillsSource)) {
            await env.Stderr.WriteLineAsync(
                $"Cannot install Codex plugin: 'skills' folder missing from {pluginPath}. " +
                InstallProvenance.ReinstallHint()
            );

            return 1;
        }

        // Per-skill preflight runs BEFORE writing hooks so a packaging defect
        // (top-level skills/ present but an individual skill folder missing)
        // can't leave the user with hooks installed and skills not. This is the
        // atomicity guarantee from — either everything installs or nothing.
        var missingSkills = AgentsSkillsInstaller.SourceNames
            .Where(name => !Directory.Exists(Path.Combine(skillsSource, name)))
            .ToList();

        if (missingSkills.Count > 0) {
            await env.Stderr.WriteLineAsync(
                $"Cannot install Codex plugin: missing skill folder(s) under {skillsSource}: "
              + string.Join(", ", missingSkills)
              + ". " + InstallProvenance.ReinstallHint()
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
            "accept once to trust them all (or run /hooks inside Codex to trust them individually). " +
            "The Codex desktop app never prompts: trust the kcap hooks in Settings → Hooks there."
        );

        // Skills are user-scoped only. Written to ~/.agents/skills/ so they
        // work across Codex and other compatible agents.
        var skillsConsumers = SharedSkillsConsumers.Read(env.Agents.UserSkillsDir, ConfiguredSkillsConsumers);

        if (!AgentsSkillsInstaller.Install(skillsSource, env.Agents.UserSkillsDir)) {
            await env.Stderr.WriteLineAsync("Could not install agent skills.");

            return 1;
        }

        SharedSkillsConsumers.Write(env.Agents.UserSkillsDir, skillsConsumers.Append(HarnessId.Codex.VendorId));

        await env.Stdout.WriteLineAsync($"Agent skills installed (user: {env.Agents.UserSkillsDir})");

        AgentsSkillsInstaller.CleanLegacyCodexSkills(codex.SkillsDir);

        // enable Codex sandbox network access so the skills just installed can
        // reach the kcap server. Opt out with --skip-codex-network-access. The
        // --if-installed refresh path returns earlier, so npm postinstall never flips this.
        if (!args.Contains("--skip-codex-network-access"))
            await EnableCodexNetworkAccessAsync();

        // Register the kcap MCP servers in ~/.codex/config.toml so Codex CLI picks them up
        // with no manual TOML edit (the plugin descriptor path alone isn't enough — many
        // users never run `codex plugin add`). Non-destructive + idempotent.
        await RegisterCodexMcpServersAsync();

        if (scope == "project") {
            await env.Stdout.WriteLineAsync(
                "Note: Codex requires the project's .codex directory to be trusted. " +
                "Run `codex` once in this directory and accept the trust prompt."
            );

            return 0;
        }

        var accountsWired = await WireUserAccountsAsync(
            HarnessId.Codex, codex.Home, adoptDefault: true, refreshOnly: false,
            enableNetwork: !args.Contains("--skip-codex-network-access"));

        return accountsWired ? 0 : 1;
    }

    /// <summary>
    /// turn on Codex's <c>workspace-write</c> sandbox network access, constrained
    /// to the kcap server(s) of every configured profile, so kcap skills can reach the
    /// server. Never fails the install: a write error is a warning, not an error code.
    /// </summary>
    async Task EnableCodexNetworkAccessAsync() {
        var codex = env.Harnesses.Of<CodexHarness>().Paths;

        var domains = env.CodexNetworkAllowDomains();

        if (domains.Count == 0) {
            await env.Stdout.WriteLineAsync(
                "No kcap server configured yet — run `kcap setup` to allow Codex network access for kcap skills.");

            return;
        }

        switch (CodexConfigToml.EnableNetworkAccess(domains, codex.ConfigToml)) {
            case CodexConfigToml.Change.Updated:
                await env.Stdout.WriteLineAsync($"Codex sandbox network access enabled for kcap ({codex.ConfigToml}).");
                break;
            case CodexConfigToml.Change.Unchanged:
                await env.Stdout.WriteLineAsync("Codex sandbox already allows network access — no change needed.");
                break;
            default:
                await env.Stderr.WriteLineAsync(
                    $"Warning: could not update {codex.ConfigToml} — enable Codex sandbox network access manually (see README).");
                break;
        }
    }

    /// <summary>
    /// Registers the kcap MCP servers (<see cref="KcapMcpServers.ForCodex"/>) in
    /// <c>~/.codex/config.toml</c> so Codex CLI loads them without a manual TOML edit.
    /// Never fails the install: a write error is a warning, not an error code.
    /// </summary>
    async Task RegisterCodexMcpServersAsync() {
        var codex = env.Harnesses.Of<CodexHarness>().Paths;

        switch (CodexConfigToml.RegisterKcapMcpServers(codex.ConfigToml, env.ResolveMcpBinaryPath)) {
            case CodexConfigToml.Change.Updated:
                await env.Stdout.WriteLineAsync($"Codex MCP servers registered: {string.Join(", ", KcapMcpServers.ForCodex.Select(s => s.Name))} ({codex.ConfigToml}).");
                break;
            case CodexConfigToml.Change.Unchanged:
                await env.Stdout.WriteLineAsync("Codex MCP servers already registered — no change needed.");
                break;
            default:
                _refreshFailed = true;
                await env.Stderr.WriteLineAsync(
                    $"Warning: could not register Codex MCP servers in {codex.ConfigToml} — see README to add them manually.");
                break;
        }
    }

    async Task<int> RemoveCodex(string[] args) {
        var codex = env.Harnesses.Of<CodexHarness>().Paths;

        var scope = args.Contains("--project") ? "project" : "user";

        var hooksPath = scope == "project"
            ? Path.Combine(workdir.Path, ".codex", "hooks.json")
            : codex.UserHooksJson;

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

        // config.toml is user-global, so only a user-scope remove touches its MCP servers and
        // sandbox network keys: every other repository relies on them.
        var mcpChange = scope == "user"
            ? CodexConfigToml.UnregisterKcapMcpServers(codex.ConfigToml)
            : CodexConfigToml.Change.Unchanged;
        var mcpFailed = mcpChange == CodexConfigToml.Change.Failed;
        var mcpChanged = mcpChange is CodexConfigToml.Change.Updated or
            CodexConfigToml.Change.UpdatedWithPreservedEntries;
        var mcpPreserved = mcpChange is CodexConfigToml.Change.UpdatedWithPreservedEntries or
            CodexConfigToml.Change.PreservedUnownedEntries or
            CodexConfigToml.Change.PreservedOwnershipUnknown;

        if (mcpChanged) {
            await env.Stdout.WriteLineAsync($"Codex MCP servers removed ({codex.ConfigToml})");
        }
        if (mcpPreserved) {
            var reason = mcpChange == CodexConfigToml.Change.PreservedOwnershipUnknown
                ? "the ownership ledger is missing or corrupt"
                : "one or more entries are user-owned or were edited";
            await env.Stderr.WriteLineAsync(
                $"Warning: some Codex MCP entries were preserved because {reason}. Review [mcp_servers] in {codex.ConfigToml} and remove kcap-flows manually if you no longer want the paid flow surface.");
        } else if (mcpFailed) {
            await env.Stderr.WriteLineAsync($"Could not update {codex.ConfigToml} to remove Codex MCP servers.");
        }

        var network = scope == "user"
            ? CodexConfigToml.RestoreNetworkAccess(codex.ConfigToml, env.CodexNetworkAllowDomains())
            : CodexNetworkRestore.Nothing;
        var networkFailed = network.Change == CodexConfigToml.Change.Failed;

        await ReportNetworkRestoreAsync(network, codex.ConfigToml);

        if (scope == "user") CodexConfigToml.DeleteEmptyOwnershipLedger(codex.ConfigToml);

        // The skills are user-wide, so a project remove leaves them to a user-scope Codex still installed.
        var agents = scope == "user" || !CodexHooksInstaller.IsInstalled(codex.UserHooksJson)
            ? await ReleaseSharedSkillsAsync(HarnessId.Codex)
            : new RemovalResult(false, false);

        var legacy = AgentsSkillsInstaller.CleanLegacyCodexSkills(codex.SkillsDir);

        var accountsFailed = scope == "user" && !await UnwireUserAccountsAsync(HarnessId.Codex, codex.Home);

        if (hooksFailed || mcpFailed || networkFailed || agents.HadErrors || legacy.HadErrors || accountsFailed) {
            await env.Stdout.WriteLineAsync("Removal incomplete — see errors above.");

            return 1;
        }

        if (!hooksRemoved && !mcpChanged && !mcpPreserved && network.Restored.Count == 0 && network.Kept.Count == 0
         && !agents.RemovedAny && !legacy.RemovedAny) {
            await env.Stdout.WriteLineAsync("Nothing to remove — hooks and skills were not installed.");
        }

        return 0;
    }

    async Task ReportNetworkRestoreAsync(CodexNetworkRestore network, string configPath) {
        if (network.Change == CodexConfigToml.Change.Failed) {
            await env.Stderr.WriteLineAsync(
                $"Could not update {configPath} to undo kcap's Codex sandbox network settings; they were left in place.");

            return;
        }

        if (network.Restored.Count > 0)
            await env.Stdout.WriteLineAsync($"Codex sandbox network settings restored ({configPath}): {string.Join("; ", network.Restored)}");

        if (network.Kept.Count == 0) return;

        await env.Stderr.WriteLineAsync(
            $"Warning: kept Codex sandbox network settings in {configPath} that kcap cannot prove are safe to undo:");
        foreach (var kept in network.Kept)
            await env.Stderr.WriteLineAsync($"  {kept}");
        await env.Stderr.WriteLineAsync(
            "Review [sandbox_workspace_write] and [features.network_proxy] there and change them if you no longer want the sandbox to reach these hosts.");
    }

    /// <summary>Every registered account of <paramref name="vendor"/> other than the
    /// environment-derived one, which keeps its own install path. A registry that cannot be read,
    /// locked or written is a warning, never a failure: the default layout must stay installable
    /// and removable whatever state the registry is in.</summary>
    async Task<IReadOnlyList<VendorAccount>> UserAccountsAsync(HarnessId vendor, string defaultDirectory, bool adoptDefault) {
        if (env.Accounts is null) return [];

        try {
            if (adoptDefault) AccountAdoption.EnsureDefault(env.Accounts, vendor, defaultDirectory, TimeProvider.System);

            return [.. AccountAdoption.Of(env.Accounts, vendor).Where(a => !AccountDirectory.Same(a.Directory, defaultDirectory))];
        } catch (Exception ex) when (IsRegistryFailure(ex)) {
            await RegistryUnavailableAsync(ex);

            return [];
        }
    }

    static bool IsRegistryFailure(Exception ex) =>
        ex is InvalidDataException or IOException or UnauthorizedAccessException or TimeoutException or WaitHandleCannotBeOpenedException;

    /// <summary>
    /// Whether a registry failure other than an unparseable file stopped this command reaching every
    /// account. An unparseable registry names no account anyone could unwire, so deleting it loses
    /// nothing; a locked or unreadable one may still list wired accounts, and only it can unwire them.
    /// The exit code stays a warning's so <c>kcap plugin remove</c> keeps removing the default layout.
    /// </summary>
    public bool AccountsMayStillBeWired { get; private set; }

    Task RegistryUnavailableAsync(Exception ex) {
        _refreshFailed = true;
        if (ex is not InvalidDataException) AccountsMayStillBeWired = true;

        return env.Stderr.WriteLineAsync($"Could not read the kcap account registry ({ex.Message}); only the default directory was updated.");
    }

    /// <summary>Runs one account's wiring under the registry lock; null, after the registry warning, when the
    /// lock cannot be taken. Never call <see cref="AccountAdoption.EnsureDefault"/> inside: the lock is not
    /// re-entrant.</summary>
    async Task<T?> LockedAsync<T>(Func<T> wiring) where T : class {
        IDisposable lease;

        try {
            lease = env.Accounts!.Lock();
        } catch (Exception ex) when (IsRegistryFailure(ex)) {
            await RegistryUnavailableAsync(ex);

            return null;
        }

        using (lease) return wiring();
    }

    async Task<bool> WireUserAccountsAsync(HarnessId vendor, string defaultDirectory, bool adoptDefault, bool refreshOnly, bool enableNetwork) {
        WiringOptions? options = null;
        var            ok      = true;

        foreach (var account in await UserAccountsAsync(vendor, defaultDirectory, adoptDefault)) {
            options ??= Wiring(enableNetwork);

            var steps = await LockedAsync(() => WireOne(account, refreshOnly, options));
            if (steps is null) break;

            if (steps.Count > 0) ok &= await ReportAsync(account, steps, "Wired", "wire");
        }

        return ok;
    }

    /// <summary>No steps when a refresh leaves the account alone.</summary>
    IReadOnlyList<WiringStep> WireOne(VendorAccount account, bool refreshOnly, WiringOptions options) {
        if (refreshOnly) {
            // A refresh never wires an account the user unwired: only an existing install is refreshed.
            if (!IsInstalledIn(account)) return [];

            if (account.Vendor is HarnessId.Codex) {
                var paths = AccountLayouts.Codex(env.Home, account.Directory);
                if (CodexConfigToml.RegisterKcapMcpServers(paths.ConfigToml, env.ResolveMcpBinaryPath) == CodexConfigToml.Change.Failed)
                    _refreshFailed = true;
                if (!CodexHooksInstaller.IsInstalled(paths.UserHooksJson)) return [];
            }

            if (account.Vendor != HarnessId.Claude && MarkerIsCurrent(account) && !RefusedByCodex(account)) return [];
        }

        return AccountWiring.Wire(account, env.Home, options, env.Accounts!);
    }

    async Task<bool> UnwireUserAccountsAsync(HarnessId vendor, string defaultDirectory) {
        var ok = true;

        foreach (var account in await UserAccountsAsync(vendor, defaultDirectory, adoptDefault: false)) {
            var steps = await LockedAsync(() => AccountWiring.Unwire(account, env.Home, env.Accounts!));
            if (steps is null) break;

            ok &= await ReportAsync(account, steps, "Unwired", "unwire");
        }

        return ok;
    }

    bool IsInstalledIn(VendorAccount account) => account.Vendor switch {
        HarnessId.Claude => ClaudePluginInstaller.IsInstalled(AccountLayouts.Claude(env.Home, account.Directory).UserSettings),
        HarnessId.Codex  => CodexHooksInstaller.IsInstalled(AccountLayouts.Codex(env.Home, account.Directory).UserHooksJson)
            || CodexConfigToml.HasMcpInstallRecord(AccountLayouts.Codex(env.Home, account.Directory).ConfigToml),
        _                => false,
    };

    // A byte-order mark makes Codex refuse the file whatever the marker says.
    bool RefusedByCodex(VendorAccount account) =>
        account.Vendor is HarnessId.Codex
     && CodexHooksInstaller.StartsWithByteOrderMark(AccountLayouts.Codex(env.Home, account.Directory).UserHooksJson);

    bool MarkerIsCurrent(VendorAccount account) => account.Vendor switch {
        HarnessId.Claude => ClaudePluginInstaller.ReadMarker(AccountLayouts.Claude(env.Home, account.Directory).UserSettings) == CapacitorVersion.Current(),
        HarnessId.Codex  => CodexHooksInstaller.ReadMarker(AccountLayouts.Codex(env.Home, account.Directory).UserHooksJson) == CapacitorVersion.Current(),
        _                => false,
    };

    WiringOptions Wiring(bool enableNetwork) {
        var domains = enableNetwork ? env.CodexNetworkAllowDomains() : [];

        return new(env.ResolvePluginPath(), env.Agents.UserSkillsDir, env.ResolveMcpBinaryPath, domains.Count == 0 ? null : domains);
    }

    async Task<bool> ReportAsync(VendorAccount account, IReadOnlyList<WiringStep> steps, string done, string attempt) {
        var fatal = AccountWiring.HasFatalFailure(steps);
        _refreshFailed |= fatal;

        if (!fatal) await env.Stdout.WriteLineAsync($"{done} {account.Vendor} account {account.Label} ({account.Directory})");

        if (!AccountWiring.Succeeded(steps)) {
            var detail = AccountWiring.FailureSummary(steps);
            await env.Stderr.WriteLineAsync(fatal
                ? $"Could not {attempt} {account.Vendor} account {account.Label} ({account.Directory}): {detail}"
                : $"Warning: {account.Vendor} account {account.Label} ({account.Directory}): {detail}");
        }

        return !fatal;
    }

    /// <summary>
    /// Merges into <paramref name="hooksPath"/> a hooks.json entry that invokes <c>kcap hook --codex</c> for
    /// every Codex event. Non-kcap entries are preserved; kcap entries are replaced, so the timeout and
    /// command stay current after an upgrade.
    /// </summary>
    public static bool InstallCodexHooks(string hooksPath) =>
        CodexHooksWriter.Install(hooksPath) is SettingsEdit.Changed or SettingsEdit.Unchanged;

    /// <summary>
    /// Removes every entry in <paramref name="hooksPath"/> whose command
    /// invokes kcap's Codex hook. Other entries are preserved.
    /// Returns true if any entries were removed. Throws on I/O failure
    /// (caller decides how to surface partial writes); returns false only
    /// when there was genuinely nothing to remove.
    /// </summary>
    public static bool RemoveCodexHooks(string hooksPath) =>
        CodexHooksWriter.Remove(hooksPath) switch {
            SettingsEdit.Changed => true,
            SettingsEdit.Failed  => throw new IOException($"Could not write {hooksPath}."),
            _                    => false,
        };

    async Task<int> InstallCursor(string[] args) {
        var cursor    = env.Harnesses.Of<CursorHarness>().Paths;
        var hooksPath = GetArg(args, "--cursor-hooks-path") ?? cursor.UserHooksJson;

        var refreshOnly    = args.Contains("--if-installed");
        var hooksInstalled = CursorHooksInstaller.IsInstalled(hooksPath);
        var toolsOnly      = ToolsOnly(args, refreshOnly, hooksInstalled);

        if (refreshOnly && !hooksInstalled && !HarnessMcpProjections.Cursor.OwnsAnything(cursor.UserMcpJson, env.Home))
            return 0;

        // Current recording hooks do not imply that the MCP executable path is current.
        var hooksCurrent = refreshOnly && File.Exists(hooksPath)
            && CursorHooksInstaller.ReadMarker(hooksPath) == CapacitorVersion.Current();
        if (!toolsOnly && !hooksCurrent) {
            if (!refreshOnly && !KcapOnPath) {
                await env.Stderr.WriteLineAsync(
                    "Cannot install Cursor hooks: 'kcap' is not on PATH. "
                  + InstallProvenance.NotOnPathHint()
                );
                return 1;
            }

            if (!InstallCursorHooks(hooksPath)) {
                if (refreshOnly) { _refreshFailed = true; return 0; }

                await env.Stderr.WriteLineAsync("Could not write Cursor hooks file.");

                return 1;
            }

            await env.Stdout.WriteLineAsync(
                refreshOnly
                    ? $"Cursor hooks refreshed ({hooksPath})"
                    : $"Cursor hooks installed ({hooksPath})"
            );
        }

        if (!args.Contains("--skip-cursor-mcp"))
            await RegisterCursorMcpServersAsync();

        await SharedSkillsAsync(HarnessId.Cursor, args, refreshOnly);

        if (toolsOnly && !refreshOnly) await NoteCaptureSkippedAsync("Cursor");

        return 0;
    }

    /// <summary>
    /// Registers the kcap MCP servers in <c>~/.cursor/mcp.json</c> so Cursor loads them
    /// without a manual JSON edit. Never fails the install: a write error is a warning,
    /// not an error code.
    /// </summary>
    async Task RegisterCursorMcpServersAsync() {
        var cursor = env.Harnesses.Of<CursorHarness>().Paths;

        var change = HarnessMcpProjections.Cursor.Register(cursor.UserMcpJson, env.Home, resolveBinaryPath: env.ResolveMcpBinaryPath);

        switch (change) {
            case JsonMcpConfigWriter.Change.Updated:
                await env.Stdout.WriteLineAsync($"Cursor MCP servers registered ({cursor.UserMcpJson}).");
                break;
            case JsonMcpConfigWriter.Change.Failed:
                _refreshFailed = true;
                await env.Stderr.WriteLineAsync(
                    $"Warning: could not update {cursor.UserMcpJson} to register Cursor MCP servers.");
                break;
            // Unchanged: silent — same as Codex's already-registered case.
        }
    }

    async Task<int> RemoveCursor(string[] args) {
        var cursor = env.Harnesses.Of<CursorHarness>().Paths;

        var hooksPath = GetArg(args, "--cursor-hooks-path") ?? cursor.UserHooksJson;

        var hooksFailed = false;

        if (!File.Exists(hooksPath)) {
            await env.Stdout.WriteLineAsync("Nothing to remove — Cursor hooks file not found.");
        } else {
            try {
                var removed = RemoveCursorHooks(hooksPath);

                await env.Stdout.WriteLineAsync(
                    removed
                        ? $"Cursor hooks removed ({hooksPath})"
                        : "Cursor hooks were not installed."
                );
            } catch (Exception ex) {
                await env.Stderr.WriteLineAsync($"Could not update Cursor hooks at {hooksPath}: {ex.Message}");
                hooksFailed = true;
            }
        }

        // Cursor is user-scope only (no --project split like Codex), so the kcap MCP
        // entries are always unregistered here, independent of whether hooks.json
        // existed — the two files are unrelated on disk.
        var mcpChange = HarnessMcpProjections.Cursor.Unregister(cursor.UserMcpJson, env.Home);
        var mcpFailed = mcpChange == JsonMcpConfigWriter.Change.Failed;

        if (mcpChange == JsonMcpConfigWriter.Change.Updated) {
            await env.Stdout.WriteLineAsync($"Cursor MCP servers removed ({cursor.UserMcpJson})");
        } else if (mcpFailed) {
            await env.Stderr.WriteLineAsync($"Could not update {cursor.UserMcpJson} to remove Cursor MCP servers.");
        }

        var skills = await ReleaseSharedSkillsAsync(HarnessId.Cursor);

        return hooksFailed || mcpFailed || skills.HadErrors ? 1 : 0;
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

    async Task<int> InstallPi(string[] args) {
        var pi = env.Harnesses.Of<PiHarness>().Paths;

        var extensionPath    = GetArg(args, "--pi-extension-path") ?? pi.KcapExtension;
        var mcpExtensionPath = pi.KcapMcpExtension;

        var refreshOnly      = args.Contains("--if-installed");
        var skipMcp          = args.Contains("--skip-pi-mcp");
        var skipInstructions = args.Contains("--skip-pi-instructions");
        var ingestInstalled  = PiExtensionInstaller.IsInstalled(extensionPath);
        var toolsOnly        = ToolsOnly(args, refreshOnly, ingestInstalled);

        // A refresh never touches a machine that never opted into Pi; once it has, the refresh also
        // heals the MCP bridge + AGENTS.md steering below.
        if (refreshOnly && !ingestInstalled
         && !PiMcpExtensionInstaller.IsInstalled(mcpExtensionPath) && !AgentInstructionsWriter.IsInstalled(pi.AgentsMd))
            return 0;

        // No stale-session report for Pi: it runs through a node shim, and node's process.title setter
        // rewrites the argv region — so the process is named `pi` with a command line of just "pi", or
        // named `node` with the package path intact, never both. A name this generic needs
        // corroboration, and the only corroborating signal disappears exactly when the name appears.

        // Both extensions shell out to the bare `kcap` (ingest → `kcap hook --pi`, bridge → `kcap mcp
        // <name>`), so even --tools-only needs it unless the bridge is skipped. Not checked on the
        // postinstall path.
        if (!refreshOnly && (!toolsOnly || !skipMcp) && !KcapOnPath) {
            await env.Stderr.WriteLineAsync(
                "Cannot install the Pi extension: 'kcap' is not on PATH. "
              + InstallProvenance.NotOnPathHint()
            );

            return 1;
        }

        var extensionFailed = false;

        // 1. Live-ingest extension (kcap.ts). On refresh, skip only when already current.
        var ingestCurrent = refreshOnly
                         && ingestInstalled
                         && PiExtensionInstaller.ReadMarker(extensionPath) == CapacitorVersion.Current();
        if (!toolsOnly && !ingestCurrent) {
            if (PiExtensionInstaller.Install(extensionPath)) {
                await env.Stdout.WriteLineAsync(
                    refreshOnly
                        ? $"Pi extension refreshed ({extensionPath})"
                        : $"Pi extension installed ({extensionPath})"
                );
            } else if (!refreshOnly) {
                // Fresh install: the ingest write failed. Report + return non-zero, but
                // DON'T bail — the independent MCP bridge + AGENTS.md still install below.
                await env.Stderr.WriteLineAsync("Could not write the Pi extension file.");
                extensionFailed = true;
            } else {
                _refreshFailed = true;
                await env.Stderr.WriteLineAsync(
                    $"Warning: could not refresh the Pi extension ({extensionPath}); continuing with MCP + instructions.");
            }
        }

        // 2. MCP-bridge extension (kcap-mcp.ts) — a separate file + marker, healed
        //    independently. Non-fatal: a write error is a warning, not an error code.
        if (!skipMcp)
            await InstallPiMcpExtensionAsync(mcpExtensionPath, refreshOnly);

        // 3. Agent-instructions block in ~/.pi/agent/AGENTS.md so Pi's model is steered
        //    toward the kcap tools. Non-destructive (only our block) + idempotent. Never fails.
        if (!skipInstructions)
            await InstallPiInstructionsAsync();

        await SharedSkillsAsync(HarnessId.Pi, args, refreshOnly);

        if (toolsOnly && !refreshOnly) await NoteCaptureSkippedAsync("Pi");

        // Non-zero only when a FRESH ingest install failed (the integration is incomplete) —
        // the independent MCP bridge + AGENTS.md steering above were still installed.
        return extensionFailed ? 1 : 0;
    }

    /// <summary>
    /// Installs the kcap MCP-bridge extension (<c>~/.pi/agent/extensions/kcap-mcp.ts</c>).
    /// Its own file + version marker, so on refresh it is skipped only when already at the
    /// current version. Never fails the install: a write error is a warning.
    /// </summary>
    async Task InstallPiMcpExtensionAsync(string mcpExtensionPath, bool refreshOnly) {
        // Require the extension FILE itself (not just a current marker) for the "already current"
        // fast path — otherwise a deleted kcap-mcp.ts with a stale-but-current marker would skip the
        // heal and never recreate the file. (Marker-only state is only the opt-in signal in InstallPi.)
        if (refreshOnly
            && File.Exists(mcpExtensionPath)
            && PiMcpExtensionInstaller.ReadMarker(mcpExtensionPath) == CapacitorVersion.Current())
            return;

        if (PiMcpExtensionInstaller.Install(mcpExtensionPath)) {
            await env.Stdout.WriteLineAsync(
                refreshOnly
                    ? $"Pi MCP extension refreshed ({mcpExtensionPath})"
                    : $"Pi MCP extension installed ({mcpExtensionPath})"
            );
        } else {
            _refreshFailed = true;
            await env.Stderr.WriteLineAsync(
                $"Warning: could not write the Pi MCP extension ({mcpExtensionPath}).");
        }
    }

    /// <summary>
    /// Installs kcap's marker-delimited instructions block into <c>~/.pi/agent/AGENTS.md</c>
    /// (Pi's native user-global instructions file) so Pi's model is steered toward the kcap
    /// tools. Non-destructive (only our block). Never fails the install: a write error is a warning.
    /// </summary>
    async Task InstallPiInstructionsAsync() {
        var pi = env.Harnesses.Of<PiHarness>().Paths;

        var change = AgentInstructionsWriter.Write(pi.AgentsMd, KcapAgentInstructions.Body);

        switch (change) {
            case AgentInstructionsWriter.Change.Updated:
                await env.Stdout.WriteLineAsync($"Pi instructions installed ({pi.AgentsMd}).");
                break;
            case AgentInstructionsWriter.Change.Failed:
                _refreshFailed = true;
                await env.Stderr.WriteLineAsync(
                    $"Warning: could not update {pi.AgentsMd} to install Pi instructions.");
                break;
            // Unchanged: silent.
        }
    }

    async Task<int> RemovePi(string[] args) {
        var pi = env.Harnesses.Of<PiHarness>().Paths;

        var extensionPath    = GetArg(args, "--pi-extension-path") ?? pi.KcapExtension;
        var mcpExtensionPath = pi.KcapMcpExtension;

        var failed = false;

        // 1. Live-ingest extension.
        try {
            var removed = PiExtensionInstaller.Remove(extensionPath);

            await env.Stdout.WriteLineAsync(
                removed
                    ? $"Pi extension removed ({extensionPath})"
                    : "Nothing to remove — Pi extension file not found."
            );
        } catch (Exception ex) {
            await env.Stderr.WriteLineAsync($"Could not remove the Pi extension at {extensionPath}: {ex.Message}");
            failed = true;
        }

        // 2. MCP-bridge extension.
        try {
            if (PiMcpExtensionInstaller.Remove(mcpExtensionPath))
                await env.Stdout.WriteLineAsync($"Pi MCP extension removed ({mcpExtensionPath})");
        } catch (Exception ex) {
            await env.Stderr.WriteLineAsync($"Could not remove the Pi MCP extension at {mcpExtensionPath}: {ex.Message}");
            failed = true;
        }

        // 3. Instructions block in ~/.pi/agent/AGENTS.md — strip our block, preserving user content.
        var instrChange = AgentInstructionsWriter.Remove(pi.AgentsMd);
        if (instrChange == AgentInstructionsWriter.Change.Updated) {
            await env.Stdout.WriteLineAsync($"Pi instructions removed ({pi.AgentsMd}).");
        } else if (instrChange == AgentInstructionsWriter.Change.Failed) {
            await env.Stderr.WriteLineAsync($"Could not update {pi.AgentsMd} to remove Pi instructions.");
            failed = true;
        }

        if ((await ReleaseSharedSkillsAsync(HarnessId.Pi)).HadErrors) failed = true;

        return failed ? 1 : 0;
    }

    // ── OpenCode (SST): a TypeScript plugin, not a hooks.json ───────

    async Task<int> InstallOpenCode(string[] args) {
        var opencode   = env.Harnesses.Of<OpenCodeHarness>().Paths;
        var pluginPath = GetArg(args, "--opencode-plugin-path") ?? opencode.KcapPlugin;

        var refreshOnly     = args.Contains("--if-installed");
        var pluginInstalled = OpenCodeExtensionInstaller.IsInstalled(pluginPath);
        var toolsOnly       = ToolsOnly(args, refreshOnly, pluginInstalled);

        // Refresh-only mode never touches a machine that never opted in.
        if (refreshOnly && !pluginInstalled
         && !HarnessMcpProjections.OpenCode.OwnsAnything(opencode.McpConfigJson, env.Home)
         && !AgentInstructionsWriter.IsInstalled(opencode.AgentsMd))
            return 0;

        // The plugin shells out to the bare `kcap hook --opencode`; the MCP entries carry an absolute
        // path. Not checked on the postinstall path.
        if (!refreshOnly && !toolsOnly && !KcapOnPath) {
            await env.Stderr.WriteLineAsync(
                "Cannot install the OpenCode plugin: 'kcap' is not on PATH. "
              + InstallProvenance.NotOnPathHint()
            );

            return 1;
        }

        // Write the plugin unless a refresh finds it already on disk AND at the current version.
        // The File.Exists guard matters because OpenCodeExtensionInstaller.IsInstalled treats a lone
        // marker as "installed" — so a deleted kcap.ts with a current marker must still be rewritten,
        // not skipped. Even when the plugin write IS skipped, still (re)register MCP + install
        // instructions below: they live in separate files and must be healed if deleted or failed.
        var pluginCurrent = refreshOnly
            && File.Exists(pluginPath)
            && OpenCodeExtensionInstaller.ReadMarker(pluginPath) == CapacitorVersion.Current();
        if (!toolsOnly && !pluginCurrent) {
            if (OpenCodeExtensionInstaller.Install(pluginPath)) {
                await env.Stdout.WriteLineAsync(
                    refreshOnly
                        ? $"OpenCode plugin refreshed ({pluginPath})"
                        : $"OpenCode plugin installed ({pluginPath})"
                );
            } else if (!refreshOnly) {
                // Fresh install: the plugin is the whole point — fail.
                await env.Stderr.WriteLineAsync("Could not write the OpenCode plugin file.");

                return 1;
            } else {
                // Refresh: the plugin write failed, but MCP + instructions live in separate files and
                // are independent + idempotent — warn and still heal them below rather than bail.
                _refreshFailed = true;
                await env.Stderr.WriteLineAsync(
                    $"Warning: could not refresh the OpenCode plugin ({pluginPath}); continuing with MCP + instructions.");
            }
        }

        // Register the kcap MCP servers in ~/.config/opencode/opencode.json so OpenCode picks them
        // up with no manual JSON edit. Non-destructive + idempotent. Never fails the install:
        // a write error is a warning, not an error code (mirrors Cursor/Copilot).
        if (!args.Contains("--skip-opencode-mcp"))
            await RegisterOpenCodeMcpServersAsync();

        // Install kcap's agent-instructions block so OpenCode's model is steered toward the kcap MCP
        // tools. Non-destructive (only our marker block) + idempotent. Never fails the install.
        if (!args.Contains("--skip-opencode-instructions"))
            await InstallOpenCodeInstructionsAsync();

        await SharedSkillsAsync(HarnessId.OpenCode, args, refreshOnly);

        if (toolsOnly && !refreshOnly) await NoteCaptureSkippedAsync("OpenCode");

        return 0;
    }

    /// <summary>
    /// Registers the kcap MCP servers in OpenCode's <c>~/.config/opencode/opencode.json</c>
    /// (<c>mcp</c> block, <c>type:"local"</c>, command-as-array, <c>enabled:true</c>) so OpenCode
    /// loads them without a manual JSON edit. Never fails the install: a write error is a warning.
    /// </summary>
    async Task RegisterOpenCodeMcpServersAsync() {
        var opencode = env.Harnesses.Of<OpenCodeHarness>().Paths;

        var change = HarnessMcpProjections.OpenCode.Register(opencode.McpConfigJson, env.Home, resolveBinaryPath: env.ResolveMcpBinaryPath);

        switch (change) {
            case JsonMcpConfigWriter.Change.Updated:
                await env.Stdout.WriteLineAsync($"OpenCode MCP servers registered ({opencode.McpConfigJson}).");
                break;
            case JsonMcpConfigWriter.Change.Failed:
                _refreshFailed = true;
                await env.Stderr.WriteLineAsync(
                    $"Warning: could not update {opencode.McpConfigJson} to register OpenCode MCP servers.");
                break;
            // Unchanged: silent.
        }
    }

    /// <summary>
    /// Installs kcap's marker-delimited instructions block into OpenCode's user-global
    /// <c>~/.config/opencode/AGENTS.md</c>. Non-destructive (only our block). Never fails the install.
    /// </summary>
    async Task InstallOpenCodeInstructionsAsync() {
        var opencode = env.Harnesses.Of<OpenCodeHarness>().Paths;

        var change = AgentInstructionsWriter.Write(opencode.AgentsMd, KcapAgentInstructions.Body);

        switch (change) {
            case AgentInstructionsWriter.Change.Updated:
                await env.Stdout.WriteLineAsync($"OpenCode instructions installed ({opencode.AgentsMd}).");
                break;
            case AgentInstructionsWriter.Change.Failed:
                _refreshFailed = true;
                await env.Stderr.WriteLineAsync(
                    $"Warning: could not update {opencode.AgentsMd} to install OpenCode instructions.");
                break;
            // Unchanged: silent.
        }
    }

    async Task<int> RemoveOpenCode(string[] args) {
        var opencode = env.Harnesses.Of<OpenCodeHarness>().Paths;

        var pluginPath = GetArg(args, "--opencode-plugin-path") ?? opencode.KcapPlugin;

        var pluginFailed = false;

        try {
            var removed = OpenCodeExtensionInstaller.Remove(pluginPath);

            await env.Stdout.WriteLineAsync(
                removed
                    ? $"OpenCode plugin removed ({pluginPath})"
                    : "Nothing to remove — OpenCode plugin file not found."
            );
        } catch (Exception ex) {
            await env.Stderr.WriteLineAsync($"Could not remove the OpenCode plugin at {pluginPath}: {ex.Message}");
            pluginFailed = true;
        }

        // MCP servers live in a separate file (~/.config/opencode/opencode.json) — unregister
        // regardless (Unregister owns the ownership-marker cleanup and no-ops when the file is absent).
        var mcpChange = HarnessMcpProjections.OpenCode.Unregister(opencode.McpConfigJson, env.Home);
        var mcpFailed = mcpChange == JsonMcpConfigWriter.Change.Failed;

        if (mcpChange == JsonMcpConfigWriter.Change.Updated) {
            await env.Stdout.WriteLineAsync($"OpenCode MCP servers removed ({opencode.McpConfigJson}).");
        } else if (mcpFailed) {
            await env.Stderr.WriteLineAsync($"Could not update {opencode.McpConfigJson} to remove OpenCode MCP servers.");
        }

        // Strip kcap's instructions block from ~/.config/opencode/AGENTS.md, preserving user content.
        var instrChange = AgentInstructionsWriter.Remove(opencode.AgentsMd);
        var instrFailed = instrChange == AgentInstructionsWriter.Change.Failed;

        if (instrChange == AgentInstructionsWriter.Change.Updated) {
            await env.Stdout.WriteLineAsync($"OpenCode instructions removed ({opencode.AgentsMd}).");
        } else if (instrFailed) {
            await env.Stderr.WriteLineAsync($"Could not update {opencode.AgentsMd} to remove OpenCode instructions.");
        }

        var skills = await ReleaseSharedSkillsAsync(HarnessId.OpenCode);

        return pluginFailed || mcpFailed || instrFailed || skills.HadErrors ? 1 : 0;
    }

    // ── Antigravity — a named block in Antigravity's hooks.json ────────
    async Task<int> InstallAntigravity(string[] args) {
        var agy       = env.Harnesses.Of<AntigravityHarness>().Paths;
        var hooksPath = GetArg(args, "--antigravity-hooks-path") ?? agy.GlobalHooksJson;

        var refreshOnly    = args.Contains("--if-installed");
        var hooksInstalled = AntigravityHooksInstaller.IsInstalled(hooksPath);
        var toolsOnly      = ToolsOnly(args, refreshOnly, hooksInstalled);

        // Refresh-only mode never touches a machine that never opted in. GEMINI.md is no signal:
        // the Gemini CLI writes the same block there.
        if (refreshOnly && !hooksInstalled
         && !HarnessMcpProjections.Antigravity.OwnsAnything(agy.McpConfigJson, env.Home)
         && !AgentsSkillsInstaller.IsInstalled(agy.SkillsDir))
            return 0;

        // hooks.json runs the bare `kcap hook --antigravity`. Not checked on the postinstall path.
        if (!refreshOnly && !toolsOnly && !KcapOnPath) {
            await env.Stderr.WriteLineAsync(
                "Cannot install Antigravity hooks: 'kcap' is not on PATH. "
              + InstallProvenance.NotOnPathHint()
            );

            return 1;
        }

        // Treat hooks as current only when the host file ALSO exists — a lone marker (plugin
        // hooks.json deleted) must not let a refresh skip the rewrite and leave hooks missing.
        // MCP (mcp_config.json), instructions (GEMINI.md), and skills (~/.gemini/skills) all live
        // in SEPARATE files, so we heal them below even when the hooks write fails.
        var hooksCurrent = refreshOnly && File.Exists(hooksPath)
                        && AntigravityHooksInstaller.ReadMarker(hooksPath) == CapacitorVersion.Current();
        var freshHookFailure = false;
        if (!toolsOnly && !hooksCurrent) {
            if (InstallAntigravityHooks(hooksPath)) {
                await env.Stdout.WriteLineAsync(
                    refreshOnly
                        ? $"Antigravity hooks refreshed ({hooksPath})"
                        : $"Antigravity hooks installed ({hooksPath})"
                );
            } else if (!refreshOnly) {
                await env.Stderr.WriteLineAsync($"Could not install Antigravity hooks at {hooksPath}.");
                freshHookFailure = true;  // don't bail — the independent MCP/instructions/skills still install
            } else {
                _refreshFailed = true;
                await env.Stderr.WriteLineAsync(
                    $"Warning: could not refresh Antigravity hooks ({hooksPath}); continuing with MCP + instructions + skills.");
            }
        }

        // Register the kcap MCP servers into Antigravity's OWN ~/.gemini/config/mcp_config.json
        // (Standard shape — NOT the Gemini CLI's settings.json). Non-destructive + idempotent.
        if (!args.Contains("--skip-antigravity-mcp"))
            await RegisterAntigravityMcpServersAsync();

        // Install kcap's steering block into the shared ~/.gemini/GEMINI.md (Antigravity + Gemini
        // both read it; the marker block is single + idempotent).
        if (!args.Contains("--skip-antigravity-instructions"))
            await InstallAntigravityInstructionsAsync();

        // Install kcap skills into ~/.gemini/skills — Antigravity does NOT read ~/.agents/skills.
        await OwnSkillsAsync(agy.SkillsDir, "Antigravity", args.Contains("--skip-antigravity-skills"), refreshOnly);

        if (toolsOnly && !refreshOnly) await NoteCaptureSkippedAsync("Antigravity");

        return freshHookFailure ? 1 : 0;
    }

    /// <summary>Registers the kcap MCP servers in Antigravity's own <c>~/.gemini/config/mcp_config.json</c>
    /// (Standard shape). Never fails the install: a write error is a warning.</summary>
    async Task RegisterAntigravityMcpServersAsync() {
        var agy = env.Harnesses.Of<AntigravityHarness>().Paths;

        var change = HarnessMcpProjections.Antigravity.Register(agy.McpConfigJson, env.Home, resolveBinaryPath: env.ResolveMcpBinaryPath);

        switch (change) {
            case JsonMcpConfigWriter.Change.Updated:
                await env.Stdout.WriteLineAsync($"Antigravity MCP servers registered ({agy.McpConfigJson}).");
                break;
            case JsonMcpConfigWriter.Change.Failed:
                _refreshFailed = true;
                await env.Stderr.WriteLineAsync(
                    $"Warning: could not update {agy.McpConfigJson} to register Antigravity MCP servers.");
                break;
        }
    }

    /// <summary>Installs kcap's marker-delimited steering block into the shared <c>~/.gemini/GEMINI.md</c>.
    /// Non-destructive (only our block). Never fails the install: a write error is a warning.</summary>
    async Task InstallAntigravityInstructionsAsync() {
        var agy = env.Harnesses.Of<AntigravityHarness>().Paths;

        var change = AgentInstructionsWriter.Write(agy.InstructionsMd, KcapAgentInstructions.Body);

        switch (change) {
            case AgentInstructionsWriter.Change.Updated:
                await env.Stdout.WriteLineAsync($"Antigravity instructions installed ({agy.InstructionsMd}).");
                break;
            case AgentInstructionsWriter.Change.Failed:
                _refreshFailed = true;
                await env.Stderr.WriteLineAsync(
                    $"Warning: could not update {agy.InstructionsMd} to install Antigravity instructions.");
                break;
        }
    }

    /// <summary>
    /// Copies the kcap skills into <paramref name="targetDir"/> for a vendor that reads a skills tree.
    /// Which tree differs by vendor — the agent-agnostic <c>~/.agents/skills</c> for most, their own
    /// for Kiro and Antigravity — so the caller names it.
    /// </summary>
    /// <remarks>
    /// A refresh tops up a tree the user already has; it never creates one. `plugin remove --skills`
    /// deletes the marker precisely so an upgrade cannot silently undo it, and the npm postinstall
    /// runs the `--if-installed` form of every vendor on each `npm install -g` — without this gate a
    /// deliberate removal would come back on a command the user never ran.
    /// Never fails the install: hooks and MCP registration are what make capture work, so a skills
    /// copy that fails is a warning and the vendor is still wired up.
    /// </remarks>
    async Task<bool> InstallVendorSkillsAsync(
            string targetDir, string label, bool refreshOnly) {
        if (refreshOnly && !AgentsSkillsInstaller.IsInstalled(targetDir)) return false;

        // The sweep runs even when the tree is already current: a Cursor-first install stamps the
        // marker, so gating it on the copy would mean the stale dir outlives every later install.
        if (targetDir == env.Agents.UserSkillsDir)
            AgentsSkillsInstaller.CleanLegacyCodexSkills(env.Harnesses.Of<CodexHarness>().Paths.SkillsDir);

        if (AgentsSkillsInstaller.IsCurrent(targetDir)) return true;

        var pluginPath = env.ResolvePluginPath();
        var src        = pluginPath is null ? null : Path.Combine(pluginPath, "skills");
        if (src is null || !Directory.Exists(src)) {
            if (!refreshOnly)
                await env.Stderr.WriteLineAsync($"Warning: could not install {label} skills — kcap plugin 'skills' folder not found.");
            return false;
        }

        if (AgentsSkillsInstaller.Install(src, targetDir)) {
            await env.Stdout.WriteLineAsync($"{label} skills installed ({targetDir}).");
            return true;
        }

        await env.Stderr.WriteLineAsync($"Warning: could not install {label} skills to {targetDir}.");
        return false;
    }

    /// <summary>
    /// The shared <c>~/.agents/skills</c> half of a vendor install. Installing records the vendor as a
    /// consumer; its skip flag releases it, and the skills go only once no other integration uses them.
    /// A refresh changes neither.
    /// </summary>
    async Task SharedSkillsAsync(HarnessId vendor, string[] args, bool refreshOnly) {
        var dir = env.Agents.UserSkillsDir;

        if (!args.Contains($"--skip-{vendor.VendorId}-skills")) {
            var before = refreshOnly ? null : SharedSkillsConsumers.Read(dir, ConfiguredSkillsConsumers);

            if (await InstallVendorSkillsAsync(dir, "Agent", refreshOnly) && before is not null)
                SharedSkillsConsumers.Write(dir, before.Append(vendor.VendorId));

            return;
        }

        if (!refreshOnly) await ReleaseSharedSkillsAsync(vendor);
    }

    IEnumerable<string> ConfiguredSkillsConsumers() => SharedSkillsConsumerProbe.Configured(env.Harnesses, env.Home);

    /// <summary>
    /// Drops <paramref name="vendor"/> as a consumer of the shared skills and reports the outcome. Every
    /// harness reading the folder sees every skill in it, so while another consumer remains the vendor
    /// keeps seeing them, and the output says so rather than claiming they were turned off.
    /// </summary>
    async Task<RemovalResult> ReleaseSharedSkillsAsync(HarnessId vendor) {
        var dir     = env.Agents.UserSkillsDir;
        var release = SharedSkillsConsumers.Release(dir, vendor.VendorId, ConfiguredSkillsConsumers);

        if (release.Remaining.Count > 0) {
            if (AgentsSkillsInstaller.IsInstalled(dir))
                await env.Stdout.WriteLineAsync(
                    $"Agent skills kept ({dir}): still used by {ConsumerNames(release.Remaining)}. "
                  + $"{HarnessRegistry.LabelOf(vendor)} reads the same shared folder, so it can still load them until the last integration using them is removed.");

            return release.Removal;
        }

        await ReportSharedSkillsRemovalAsync(release.Removal);

        return release.Removal;
    }

    async Task ReportSharedSkillsRemovalAsync(RemovalResult removal) {
        var dir = env.Agents.UserSkillsDir;

        if (removal.RemovedAny)
            await env.Stdout.WriteLineAsync($"Agent skills removed (user: {dir})");

        await ReportKeptEditsAsync(removal, dir);
    }

    async Task ReportKeptEditsAsync(RemovalResult removal, string dir) {
        if (removal.Preserved.Count > 0)
            await env.Stdout.WriteLineAsync(
                $"Kept {string.Join(", ", removal.Preserved)} in {dir}: changed since kcap installed {(removal.Preserved.Count == 1 ? "it" : "them")}.");
    }

    static string ConsumerNames(IEnumerable<string> consumers) =>
        string.Join(", ", consumers.Select(c => HarnessNames.From(c) is { } id
            ? HarnessRegistry.LabelOf(id)
            : c == SharedSkillsConsumers.Explicit ? "the standalone `kcap plugin install --skills`" : c));

    /// <summary>
    /// A skills tree only one vendor reads, under that vendor's skip flag: a fresh install with the flag
    /// takes back what an earlier install put there, as the shared tree's flag does once nobody else
    /// needs it. A refresh never changes the choice.
    /// </summary>
    async Task OwnSkillsAsync(string targetDir, string label, bool skip, bool refreshOnly) {
        if (!skip) {
            await InstallVendorSkillsAsync(targetDir, label, refreshOnly);
            return;
        }

        if (refreshOnly) return;

        var removal = AgentsSkillsInstaller.Remove(targetDir);

        if (removal.RemovedAny)
            await env.Stdout.WriteLineAsync($"{label} skills removed ({targetDir}).");
        await ReportKeptEditsAsync(removal, targetDir);
        if (removal.HadErrors) {
            _refreshFailed = true;
            await env.Stderr.WriteLineAsync($"Warning: could not fully remove {label} skills from {targetDir}.");
        }
    }

    /// <summary>
    /// Reports the sessions sampled before the install, once that install has actually landed. Saying
    /// "anything from now on is captured" after a failed one would be untrue in the direction that
    /// matters.
    /// </summary>
    async Task ReportStaleAgentsAsync(
            IReadOnlyList<StaleAgentProcess> runningBefore, bool installed) {
        if (!installed) return;

        foreach (var line in StaleAgentDetector.Describe(runningBefore)) {
            await env.Stdout.WriteLineAsync(line);
        }
    }

    async Task<int> RemoveAntigravity(string[] args) {
        var agy = env.Harnesses.Of<AntigravityHarness>().Paths;

        var hooksPath = GetArg(args, "--antigravity-hooks-path") ?? agy.GlobalHooksJson;

        var hooksFailed = false;

        try {
            var removed = RemoveAntigravityHooks(hooksPath);

            await env.Stdout.WriteLineAsync(
                removed
                    ? $"Antigravity hooks removed ({hooksPath})"
                    : "Antigravity hooks were not installed."
            );
        } catch (Exception ex) {
            await env.Stderr.WriteLineAsync($"Could not update Antigravity hooks at {hooksPath}: {ex.Message}");
            hooksFailed = true;
        }

        // MCP servers live in a separate mcp_config.json — unregister regardless (Unregister owns the
        // ownership-marker cleanup and no-ops when the file is absent).
        var mcpChange = HarnessMcpProjections.Antigravity.Unregister(agy.McpConfigJson, env.Home);
        var mcpFailed = mcpChange == JsonMcpConfigWriter.Change.Failed;

        if (mcpChange == JsonMcpConfigWriter.Change.Updated) {
            await env.Stdout.WriteLineAsync($"Antigravity MCP servers removed ({agy.McpConfigJson}).");
        } else if (mcpFailed) {
            await env.Stderr.WriteLineAsync($"Could not update {agy.McpConfigJson} to remove Antigravity MCP servers.");
        }

        // kcap's steering block lives in ~/.gemini/GEMINI.md, which is SHARED with the Gemini CLI.
        // Only strip it when Gemini itself isn't installed — otherwise removing Antigravity would
        // yank Gemini's still-wanted block (and AgentInstructionsWriter.Remove would delete GEMINI.md
        // outright if the block were its sole content). When Gemini is still installed we leave the
        // shared block in place for `remove --gemini` to handle.
        var instrFailed = false;

        if (GeminiHooksInstaller.IsInstalled(env.Harnesses.Of<GeminiHarness>().Paths.SettingsJson)) {
            await env.Stdout.WriteLineAsync(
                $"Antigravity instructions left in place ({agy.InstructionsMd}) — shared with the still-installed Gemini CLI.");
        } else {
            var instrChange = AgentInstructionsWriter.Remove(agy.InstructionsMd);
            instrFailed = instrChange == AgentInstructionsWriter.Change.Failed;

            if (instrChange == AgentInstructionsWriter.Change.Updated) {
                await env.Stdout.WriteLineAsync($"Antigravity instructions removed ({agy.InstructionsMd}).");
            } else if (instrFailed) {
                await env.Stderr.WriteLineAsync($"Could not update {agy.InstructionsMd} to remove Antigravity instructions.");
            }
        }

        // Remove the kcap skills kcap copied into ~/.gemini/skills.
        var skills = AgentsSkillsInstaller.Remove(agy.SkillsDir);
        if (skills.RemovedAny) {
            await env.Stdout.WriteLineAsync($"Antigravity skills removed ({agy.SkillsDir}).");
        } else if (skills.HadErrors) {
            await env.Stderr.WriteLineAsync($"Could not fully remove Antigravity skills from {agy.SkillsDir}.");
        }

        return hooksFailed || mcpFailed || instrFailed || skills.HadErrors ? 1 : 0;
    }

    /// <summary>Setup-step delegate: install the kcap block, reporting success as a bool.</summary>
    internal static bool InstallAntigravityHooks(string hooksPath) {
        try {
            AntigravityHooksInstaller.Install(hooksPath);
            return true;
        } catch {
            return false;
        }
    }

    static bool RemoveAntigravityHooks(string hooksPath) {
        var was = AntigravityHooksInstaller.IsInstalled(hooksPath);
        AntigravityHooksInstaller.Remove(hooksPath);
        return was;
    }

    async Task<int> InstallCopilot(string[] args) {
        var copilot   = env.Harnesses.Of<CopilotHarness>().Paths;
        var hooksPath = GetArg(args, "--copilot-hooks-path") ?? copilot.KcapHooksJson;

        var refreshOnly    = args.Contains("--if-installed");
        var hooksInstalled = CopilotHooksInstaller.IsInstalled(hooksPath);
        var toolsOnly      = ToolsOnly(args, refreshOnly, hooksInstalled);

        // Refresh-only mode never touches a machine that never opted in.
        if (refreshOnly && !hooksInstalled
         && !HarnessMcpProjections.Copilot.OwnsAnything(copilot.McpConfigJson, env.Home)
         && !AgentInstructionsWriter.IsInstalled(copilot.InstructionsMd))
            return 0;

        // kcap.json writes the bare `kcap hook --copilot`. Not checked on the postinstall path.
        if (!refreshOnly && !toolsOnly && !KcapOnPath) {
            await env.Stderr.WriteLineAsync(
                "Cannot install Copilot hooks: 'kcap' is not on PATH. "
              + InstallProvenance.NotOnPathHint()
            );

            return 1;
        }

        // Write hooks unless a refresh finds them already at the current version. Even when the
        // hooks write is skipped, still (re)register MCP + install instructions below: they live in
        // separate files and must be healed if a prior write failed (warning-only) or was deleted.
        var hooksCurrent = refreshOnly && CopilotHooksInstaller.ReadMarker(hooksPath) == CapacitorVersion.Current();
        if (!toolsOnly && !hooksCurrent) {
            if (InstallCopilotHooks(hooksPath)) {
                await env.Stdout.WriteLineAsync(
                    refreshOnly
                        ? $"Copilot hooks refreshed ({hooksPath})"
                        : $"Copilot hooks installed ({hooksPath})"
                );
            } else if (!refreshOnly) {
                // Fresh install: hooks are the whole point — fail.
                await env.Stderr.WriteLineAsync("Could not write Copilot hooks file.");

                return 1;
            } else {
                // Refresh: the hook write failed, but MCP + instructions live in separate files and
                // are independent + idempotent — warn and still heal them below rather than bail.
                _refreshFailed = true;
                await env.Stderr.WriteLineAsync(
                    $"Warning: could not refresh Copilot hooks ({hooksPath}); continuing with MCP + instructions.");
            }
        }

        // Register the kcap MCP servers in ~/.copilot/mcp-config.json so Copilot picks them
        // up with no manual JSON edit. Non-destructive + idempotent. Never fails the install:
        // a write error is a warning, not an error code (mirrors Cursor/Codex).
        if (!args.Contains("--skip-copilot-mcp"))
            await RegisterCopilotMcpServersAsync();

        // Install kcap's agent-instructions block so Copilot's model is steered toward the kcap MCP
        // tools. Non-destructive (only our marker block) + idempotent. Never fails the install.
        if (!args.Contains("--skip-copilot-instructions"))
            await InstallCopilotInstructionsAsync();

        await SharedSkillsAsync(HarnessId.Copilot, args, refreshOnly);

        if (toolsOnly && !refreshOnly) await NoteCaptureSkippedAsync("Copilot");

        return 0;
    }

    /// <summary>
    /// Registers the kcap MCP servers in <c>~/.copilot/mcp-config.json</c> so Copilot loads
    /// them without a manual JSON edit. Never fails the install: a write error is a warning,
    /// not an error code.
    /// </summary>
    async Task RegisterCopilotMcpServersAsync() {
        var copilot = env.Harnesses.Of<CopilotHarness>().Paths;

        var change = HarnessMcpProjections.Copilot.Register(copilot.McpConfigJson, env.Home, resolveBinaryPath: env.ResolveMcpBinaryPath);

        switch (change) {
            case JsonMcpConfigWriter.Change.Updated:
                await env.Stdout.WriteLineAsync($"Copilot MCP servers registered ({copilot.McpConfigJson}).");
                break;
            case JsonMcpConfigWriter.Change.Failed:
                _refreshFailed = true;
                await env.Stderr.WriteLineAsync(
                    $"Warning: could not update {copilot.McpConfigJson} to register Copilot MCP servers.");
                break;
            // Unchanged: silent — same as Cursor's already-registered case.
        }
    }

    /// <summary>
    /// Installs kcap's marker-delimited instructions block into
    /// <c>~/.copilot/copilot-instructions.md</c> so Copilot's model is steered toward the kcap
    /// tools. Non-destructive (only our block). Never fails the install: a write error is a warning.
    /// </summary>
    async Task InstallCopilotInstructionsAsync() {
        var copilot = env.Harnesses.Of<CopilotHarness>().Paths;

        var change = AgentInstructionsWriter.Write(copilot.InstructionsMd, KcapAgentInstructions.Body);

        switch (change) {
            case AgentInstructionsWriter.Change.Updated:
                await env.Stdout.WriteLineAsync($"Copilot instructions installed ({copilot.InstructionsMd}).");
                break;
            case AgentInstructionsWriter.Change.Failed:
                _refreshFailed = true;
                await env.Stderr.WriteLineAsync(
                    $"Warning: could not update {copilot.InstructionsMd} to install Copilot instructions.");
                break;
            // Unchanged: silent.
        }
    }

    async Task<int> RemoveCopilot(string[] args) {
        var copilot = env.Harnesses.Of<CopilotHarness>().Paths;

        var hooksPath = GetArg(args, "--copilot-hooks-path") ?? copilot.KcapHooksJson;

        var hooksFailed = false;

        try {
            var removed = RemoveCopilotHooks(hooksPath);

            await env.Stdout.WriteLineAsync(
                removed
                    ? $"Copilot hooks removed ({hooksPath})"
                    : "Nothing to remove — Copilot hooks file not found."
            );
        } catch (Exception ex) {
            await env.Stderr.WriteLineAsync($"Could not remove Copilot hooks at {hooksPath}: {ex.Message}");
            hooksFailed = true;
        }

        // Copilot MCP servers live in a separate file (~/.copilot/mcp-config.json), so
        // unregister them independently of whether the hooks file existed. Unregister owns
        // the ownership-marker cleanup: it clears the marker on any non-Failed outcome and
        // retains it on Failed so a retry can still identify the kcap-owned entries.
        var mcpChange = HarnessMcpProjections.Copilot.Unregister(copilot.McpConfigJson, env.Home);
        var mcpFailed = mcpChange == JsonMcpConfigWriter.Change.Failed;

        if (mcpChange == JsonMcpConfigWriter.Change.Updated) {
            await env.Stdout.WriteLineAsync($"Copilot MCP servers removed ({copilot.McpConfigJson}).");
        } else if (mcpFailed) {
            await env.Stderr.WriteLineAsync($"Could not update {copilot.McpConfigJson} to remove Copilot MCP servers.");
        }

        // Strip kcap's instructions block, preserving any user-authored content in the file.
        var instrChange = AgentInstructionsWriter.Remove(copilot.InstructionsMd);
        var instrFailed = instrChange == AgentInstructionsWriter.Change.Failed;

        if (instrChange == AgentInstructionsWriter.Change.Updated) {
            await env.Stdout.WriteLineAsync($"Copilot instructions removed ({copilot.InstructionsMd}).");
        } else if (instrFailed) {
            await env.Stderr.WriteLineAsync($"Could not update {copilot.InstructionsMd} to remove Copilot instructions.");
        }

        var skills = await ReleaseSharedSkillsAsync(HarnessId.Copilot);

        return hooksFailed || mcpFailed || instrFailed || skills.HadErrors ? 1 : 0;
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

    async Task<int> InstallKiro(string[] args) {
        var kiro = env.Harnesses.Of<KiroHarness>().Paths;

        var agentPath = GetArg(args, "--kiro-agent-path") ?? kiro.KcapAgentJson;
        var mcpPath   = GetArg(args, "--kiro-mcp-path")   ?? kiro.SettingsMcpJson;

        var refreshOnly = args.Contains("--if-installed");
        var skipSkills  = args.Contains("--skip-kiro-skills");

        // The MCP file and ~/.kiro/skills are independent of the agent clone, so `--skip-kiro-hooks`,
        // --tools-only, or a clone that failed for want of kiro-cli leaves them installed without it.
        var kiroAlreadyInstalled = KiroHooksInstaller.IsInstalled(agentPath);
        var toolsOnly            = ToolsOnly(args, refreshOnly, kiroAlreadyInstalled);

        if (refreshOnly && !kiroAlreadyInstalled
         && !HarnessMcpProjections.Kiro.OwnsAnything(mcpPath, env.Home)
         && !AgentsSkillsInstaller.IsInstalled(kiro.SkillsDir))
            return 0;

        // No agent clone, no default-agent flip, no Crew hook: those are what record sessions. The MCP
        // entries carry an absolute path, so none of this needs kcap on PATH.
        if (toolsOnly) {
            if (!args.Contains("--skip-kiro-mcp"))
                await RegisterKiroMcpServersAsync(mcpPath);
            await KiroSkillsAsync(skipSkills, refreshOnly);

            await InstallKiroCrewAsync(refreshOnly, installSkills: !skipSkills, installHook: false);

            if (!refreshOnly) await NoteCaptureSkippedAsync("Kiro");

            return 0;
        }

        // Fresh install needs kcap on PATH: the agent's hook runs the bare `kcap` command.
        if (!refreshOnly && !KcapOnPath) {
            await env.Stderr.WriteLineAsync(
                "Cannot install Kiro hooks: 'kcap' is not on PATH. "
              + InstallProvenance.NotOnPathHint()
            );

            return 1;
        }

        // Sampled after the early returns above and before anything is written: a refresh that bails
        // should not sweep the process table, and a session started DURING the install loaded the
        // integration, so it must not be reported as predating it.
        var kiroRunningBefore = kiroAlreadyInstalled
            ? []
            : env.FindStaleAgents([new StaleAgentTarget("kiro", KiroPaths.ProcessName)]);

        // Clone/refresh the agent unless a refresh finds it on disk AND current (File.Exists so a
        // deleted kcap.json is recreated). MCP is registered below regardless of the clone outcome.
        var hooksFailed = false;
        var hooksCurrent = refreshOnly && File.Exists(agentPath) && KiroHooksInstaller.ReadMarker(agentPath) == CapacitorVersion.Current();
        if (!hooksCurrent) {
            if (InstallKiroHooks(agentPath, env.Harnesses, env.Stderr)) {
                var clonedFrom = KiroHooksInstaller.ReadPreviousDefault(agentPath) ?? "your default agent";
                await env.Stdout.WriteLineAsync(
                    refreshOnly
                        ? $"Kiro hooks refreshed ({agentPath})"
                        : $"Kiro hooks installed — '{KiroAgentName}' (cloned from '{clonedFrom}') is now your default "
                        + "Kiro agent, so every session is captured. Restart kiro-cli to pick it up; "
                        + "undo with: kcap plugin remove --kiro"
                );
            } else {
                // The agent clone needs kiro-cli on PATH; if it's missing the clone fails. Warn, but
                // still register the independent MCP file below rather than bailing.
                hooksFailed = true;
                await env.Stderr.WriteLineAsync(
                    $"Warning: could not set up the Kiro '{KiroAgentName}' agent (is '{KiroHarness.CliBinary}' on PATH? "
                  + "it's needed to clone your current default agent so tool access is preserved). "
                  + "Continuing with MCP registration.");
            }
        }

        // Register the kcap MCP servers in ~/.kiro/settings/mcp.json (Standard shape) so Kiro picks
        // them up with no manual JSON edit. Non-destructive + idempotent (preserves user servers and
        // their disabled/autoApprove fields). Never fails the install: a write error is a warning.
        if (!args.Contains("--skip-kiro-mcp"))
            await RegisterKiroMcpServersAsync(mcpPath);

        // Install kcap's skills into ~/.kiro/skills so Kiro's agent is steered toward the kcap MCP
        // tools (the cloned agent's resources include skill:///~/.kiro/skills/*/SKILL.md). Independent
        // of the agent clone; non-fatal (a copy error is a warning). Mirrors the Antigravity path.
        await KiroSkillsAsync(skipSkills, refreshOnly);

        await InstallKiroCrewAsync(refreshOnly, installSkills: !skipSkills, installHook: true);

        await ReportStaleAgentsAsync(kiroRunningBefore, installed: !hooksFailed);

        // A fresh agent-clone failure is still an error exit (capture won't work without it), but the
        // independent MCP file + skills were still written above.
        _refreshFailed |= hooksFailed;
        return hooksFailed && !refreshOnly ? 1 : 0;
    }

    /// <summary>
    /// Installs kcap's skills into <c>~/.kiro/skills</c> (as <c>kcap-&lt;name&gt;/SKILL.md</c>) so Kiro's
    /// agent — whose <c>resources</c> include <c>skill:///~/.kiro/skills/*/SKILL.md</c> — is steered
    /// toward the kcap MCP tools, or takes them back out under <c>--skip-kiro-skills</c>. Never fails the
    /// install: a copy error is a warning.
    /// </summary>
    Task KiroSkillsAsync(bool skip, bool refreshOnly) =>
        OwnSkillsAsync(env.Harnesses.Of<KiroHarness>().Paths.SkillsDir, "Kiro", skip, refreshOnly);

    /// <summary>
    /// Wires Kiro Crew when it is present: its agents run this same <c>kiro-cli</c> under profiles
    /// Crew regenerates, so they never carry kcap.json's hook or read <c>~/.kiro/skills</c>. A refresh
    /// wires a Crew installed since kcap, because the Kiro opt-in covers Crew — but once kcap has wired
    /// Crew, a refresh only keeps what is still there, so a hook or skills the user deleted stay
    /// deleted. Without the hook, a refresh only tops up skills already there. Never fails the install.
    /// </summary>
    async Task InstallKiroCrewAsync(bool refreshOnly, bool installSkills, bool installHook) {
        var kiro = env.Harnesses.Of<KiroHarness>();
        var crew = kiro.Crew;
        if (!crew.IsPresent()) return;

        if (!installHook) {
            await OwnSkillsAsync(crew.SkillsDir, "Kiro Crew", skip: !installSkills, refreshOnly);

            return;
        }

        var wiredBefore = KiroCrewHookInstaller.IsInstalled(crew.SpawnHookScript) || KiroCrewHookInstaller.WasRemoved(crew.SpawnHookScript);
        var kcapDir     = env.Binaries.Resolve("kcap") is { } kcap ? Path.GetDirectoryName(kcap) : null;

        var outcome = refreshOnly && KiroCrewHookInstaller.WasRemoved(crew.SpawnHookScript)
            ? KiroCrewHookInstaller.Outcome.Unchanged
            : KiroCrewHookInstaller.Install(crew.SpawnHookScript, kcapDir);

        switch (outcome) {
            case KiroCrewHookInstaller.Outcome.Written:
                await env.Stdout.WriteLineAsync(
                    $"Kiro Crew hook installed ({crew.SpawnHookScript}). Restart Kiro Crew so its agents pick it up.");
                break;
            case KiroCrewHookInstaller.Outcome.Unowned:
                await env.Stderr.WriteLineAsync(
                    $"Warning: {crew.SpawnHookScript} exists and was not written by kcap, so Kiro Crew sessions are not recorded. "
                  + "Move it aside and re-run: kcap plugin install --kiro");
                break;
            case KiroCrewHookInstaller.Outcome.Failed:
                _refreshFailed = true;
                await env.Stderr.WriteLineAsync($"Warning: could not write the Kiro Crew hook to {crew.SpawnHookScript}.");
                break;
        }

        if (!installSkills) {
            await OwnSkillsAsync(crew.SkillsDir, "Kiro Crew", skip: true, refreshOnly);

            return;
        }

        if (!refreshOnly)
            await InstallVendorSkillsAsync(crew.SkillsDir, "Kiro Crew", refreshOnly: false);
        else if (wiredBefore)
            await InstallVendorSkillsAsync(crew.SkillsDir, "Kiro Crew", refreshOnly: true);
        else if (AgentsSkillsInstaller.IsInstalled(kiro.Paths.SkillsDir))
            await InstallVendorSkillsAsync(crew.SkillsDir, "Kiro Crew", refreshOnly: false);
    }

    /// <summary>
    /// Registers the kcap MCP servers in Kiro's <c>~/.kiro/settings/mcp.json</c> (<c>mcpServers</c>
    /// map). Non-destructive + idempotent — preserves user servers and their disabled/autoApprove
    /// fields (kcap leaves autoApprove unset). Never fails the install: a write error is a warning.
    /// </summary>
    async Task RegisterKiroMcpServersAsync(string mcpPath) {
        var change = HarnessMcpProjections.Kiro.Register(mcpPath, env.Home, resolveBinaryPath: env.ResolveMcpBinaryPath);

        switch (change) {
            case JsonMcpConfigWriter.Change.Updated:
                await env.Stdout.WriteLineAsync($"Kiro MCP servers registered ({mcpPath}).");
                break;
            case JsonMcpConfigWriter.Change.Failed:
                _refreshFailed = true;
                await env.Stderr.WriteLineAsync(
                    $"Warning: could not update {mcpPath} to register Kiro MCP servers.");
                break;
            // Unchanged: silent.
        }
    }

    async Task<int> RemoveKiro(string[] args) {
        var kiro = env.Harnesses.Of<KiroHarness>().Paths;

        var agentPath    = GetArg(args, "--kiro-agent-path")    ?? kiro.KcapAgentJson;
        var settingsPath = GetArg(args, "--kiro-settings-path") ?? KiroSettingsPathFor(agentPath);
        var mcpPath      = GetArg(args, "--kiro-mcp-path")      ?? kiro.SettingsMcpJson;

        // MCP servers live in a separate settings/mcp.json — unregister independently of the agent
        // restore/removal (Unregister owns the ownership-marker cleanup and no-ops when absent).
        var mcpChange = HarnessMcpProjections.Kiro.Unregister(mcpPath, env.Home);
        var mcpFailed = mcpChange == JsonMcpConfigWriter.Change.Failed;

        if (mcpChange == JsonMcpConfigWriter.Change.Updated) {
            await env.Stdout.WriteLineAsync($"Kiro MCP servers removed ({mcpPath}).");
        } else if (mcpFailed) {
            await env.Stderr.WriteLineAsync($"Could not update {mcpPath} to remove Kiro MCP servers.");
        }

        // Remove kcap's skills from ~/.kiro/skills (independent of the agent restore).
        var skills = AgentsSkillsInstaller.Remove(kiro.SkillsDir);
        if (skills.RemovedAny) {
            await env.Stdout.WriteLineAsync($"Kiro skills removed ({kiro.SkillsDir}).");
        } else if (skills.HadErrors) {
            await env.Stderr.WriteLineAsync($"Could not fully remove Kiro skills from {kiro.SkillsDir}.");
        }

        var crew       = env.Harnesses.Of<KiroHarness>().Crew;
        var crewFailed = false;

        switch (KiroCrewHookInstaller.Remove(crew.SpawnHookScript)) {
            case KiroCrewHookInstaller.Outcome.Removed:
                await env.Stdout.WriteLineAsync($"Kiro Crew hook removed ({crew.SpawnHookScript}).");
                break;
            case KiroCrewHookInstaller.Outcome.Failed:
                crewFailed = true;
                await env.Stderr.WriteLineAsync($"Could not remove the Kiro Crew hook at {crew.SpawnHookScript}.");
                break;
        }

        var crewSkills = AgentsSkillsInstaller.Remove(crew.SkillsDir);
        if (crewSkills.RemovedAny) {
            await env.Stdout.WriteLineAsync($"Kiro Crew skills removed ({crew.SkillsDir}).");
        } else if (crewSkills.HadErrors) {
            crewFailed = true;
            await env.Stderr.WriteLineAsync($"Could not fully remove Kiro Crew skills from {crew.SkillsDir}.");
        }

        try {
            // Restore the default agent kcap replaced (recorded at install time).
            // If kcap is currently the default and the restore write FAILS, abort
            // before deleting kcap.json / the marker — otherwise chat.defaultAgent
            // is left pointing at a deleted agent and the recorded previous default
            // (marker line 2) is gone, so a retry can't recover. Leaving both in
            // place keeps `kcap plugin remove --kiro` retryable.
            var previousDefault = KiroHooksInstaller.ReadPreviousDefault(agentPath) ?? "kiro_default";
            // Strict only while there is an agent to keep for a retry; without one, a default still
            // naming kcap points at a missing agent and is reset anyway.
            var currentDefault = KiroHooksInstaller.IsInstalled(agentPath) || File.Exists(agentPath)
                ? KiroSettings.ReadDefaultAgentStrict(settingsPath)
                : KiroSettings.ReadDefaultAgent(settingsPath);
            if (currentDefault == KiroAgentName
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

            return mcpFailed || skills.HadErrors || crewFailed ? 1 : 0;
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
    public static bool InstallKiroHooks(string agentJsonPath, HarnessRegistry harnesses, TextWriter stderr) {
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

            // Clone the current default into kcap's Kiro agent (kiro-cli writes it to
            // the global agents dir, preserving tools/prompt). Skipped if kcap exists.
            if (!File.Exists(agentJsonPath)) {
                // The resolved path, not the bare name: CreateProcess appends only .exe, so a name
                // the probe matched through PATHEXT would still fail to launch.
                if (harnesses.ResolveExecutable(HarnessId.Kiro) is not { } kiroCli) return false;
                if (RunKiroCli(kiroCli, "agent", "create", KiroAgentName, "--from", recordedDefault) != 0
                 || !File.Exists(agentJsonPath))
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
                    stderr.WriteLine(
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
    internal static string KiroSettingsPathFor(string agentJsonPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(agentJsonPath)!)!, "settings", "cli.json");

    static int RunKiroCli(string kiroCliPath, params string[] arguments) {
        try {
            // ArgumentList (not a concatenated string) so a default-agent name with
            // whitespace/quotes survives as ONE argument — `ProcessStartInfo(file,
            // string)` would split "My Agent" into two args and break the clone.
            var psi = new ProcessStartInfo(kiroCliPath) {
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

    async Task<int> InstallGemini(string[] args) {
        var settingsPath = GetArg(args, "--gemini-settings-path") ?? env.Harnesses.Of<GeminiHarness>().Paths.SettingsJson;

        var refreshOnly    = args.Contains("--if-installed");
        var hooksInstalled = GeminiHooksInstaller.IsInstalled(settingsPath);
        var toolsOnly      = ToolsOnly(args, refreshOnly, hooksInstalled);

        // Refresh-only mode never touches a machine that never opted in. GEMINI.md is no signal:
        // Antigravity writes the same block there.
        if (refreshOnly && !hooksInstalled && !HarnessMcpProjections.Gemini.OwnsAnything(settingsPath, env.Home))
            return 0;

        // settings.json writes the bare `kcap hook --gemini`. Not checked on the postinstall path.
        if (!refreshOnly && !toolsOnly && !KcapOnPath) {
            await env.Stderr.WriteLineAsync(
                "Cannot install Gemini hooks: 'kcap' is not on PATH. "
              + InstallProvenance.NotOnPathHint()
            );

            return 1;
        }

        // Write hooks unless a refresh finds them already at the current version. Even when the hooks
        // write is skipped, still (re)register MCP + install instructions below: MCP shares settings.json
        // but instructions live in a separate GEMINI.md, and both must be healed if a prior write failed
        // (warning-only) or was deleted.
        // Treat hooks as "current" only when the host file ALSO exists. A lone marker (settings.json
        // deleted by hand) must NOT let a refresh skip the hook write — otherwise the MCP registration
        // below would recreate settings.json with only `mcpServers`, leaving hooks missing while the
        // marker stays current, so later refreshes never restore them.
        var hooksCurrent = refreshOnly && File.Exists(settingsPath)
                        && GeminiHooksInstaller.ReadMarker(settingsPath) == CapacitorVersion.Current();
        var freshHookFailure = false;
        if (!toolsOnly && !hooksCurrent) {
            if (InstallGeminiHooks(settingsPath)) {
                await env.Stdout.WriteLineAsync(
                    refreshOnly
                        ? $"Gemini hooks refreshed ({settingsPath})"
                        : $"Gemini hooks installed ({settingsPath})"
                );
            } else if (!refreshOnly) {
                // Fresh install: the shared settings.json hook write failed (e.g. invalid JSON). Report
                // it and return non-zero, but DON'T bail early — the independent ~/.gemini/GEMINI.md
                // block still installs below (MCP shares settings.json, so it just fails-closed too).
                await env.Stderr.WriteLineAsync(
                    $"Could not install Gemini hooks. If {settingsPath} exists, make sure it is valid JSON — "
                  + "kcap leaves an unparseable settings.json untouched rather than overwrite your settings. "
                  + "Fix or remove it, then re-run."
                );

                freshHookFailure = true;
            } else {
                // Refresh: the hook write failed, but instructions live in a separate file and MCP is
                // independent + idempotent — warn and still heal them below rather than bail.
                _refreshFailed = true;
                await env.Stderr.WriteLineAsync(
                    $"Warning: could not refresh Gemini hooks ({settingsPath}); continuing with MCP + instructions.");
            }
        }

        // Register the kcap MCP servers into the shared ~/.gemini/settings.json (mcpServers block) so
        // Gemini picks them up with no manual JSON edit. Non-destructive + idempotent. Never fails the
        // install: a write error is a warning, not an error code (mirrors Cursor/Copilot).
        if (!args.Contains("--skip-gemini-mcp"))
            await RegisterGeminiMcpServersAsync(settingsPath);

        // Install kcap's agent-instructions block into ~/.gemini/GEMINI.md so Gemini's model is steered
        // toward the kcap MCP tools. Non-destructive (only our marker block) + idempotent. Never fails.
        if (!args.Contains("--skip-gemini-instructions"))
            await InstallGeminiInstructionsAsync();

        await SharedSkillsAsync(HarnessId.Gemini, args, refreshOnly);

        if (toolsOnly && !refreshOnly) await NoteCaptureSkippedAsync("Gemini");

        // Non-zero only when a FRESH hook install failed (the integration is incomplete) — the
        // independent GEMINI.md steering above was still installed.
        return freshHookFailure ? 1 : 0;
    }

    /// <summary>
    /// Registers the kcap MCP servers in the shared <c>~/.gemini/settings.json</c> (<c>mcpServers</c>
    /// block, Standard shape) so Gemini loads them without a manual JSON edit. Writes to the SAME
    /// <paramref name="settingsPath"/> the hooks use (honoring <c>--gemini-settings-path</c>).
    /// Never fails the install: a write error is a warning, not an error code.
    /// </summary>
    async Task RegisterGeminiMcpServersAsync(string settingsPath) {
        var change = HarnessMcpProjections.Gemini.Register(settingsPath, env.Home, resolveBinaryPath: env.ResolveMcpBinaryPath);

        switch (change) {
            case JsonMcpConfigWriter.Change.Updated:
                await env.Stdout.WriteLineAsync($"Gemini MCP servers registered ({settingsPath}).");
                break;
            case JsonMcpConfigWriter.Change.Failed:
                _refreshFailed = true;
                await env.Stderr.WriteLineAsync(
                    $"Warning: could not update {settingsPath} to register Gemini MCP servers.");
                break;
            // Unchanged: silent — same as Cursor/Copilot's already-registered case.
        }
    }

    /// <summary>
    /// Installs kcap's marker-delimited instructions block into <c>~/.gemini/GEMINI.md</c> (Gemini's
    /// global context file) so Gemini's model is steered toward the kcap tools. Non-destructive (only
    /// our block). Never fails the install: a write error is a warning.
    /// </summary>
    async Task InstallGeminiInstructionsAsync() {
        var gemini = env.Harnesses.Of<GeminiHarness>().Paths;

        var change = AgentInstructionsWriter.Write(gemini.GeminiMd, KcapAgentInstructions.Body);

        switch (change) {
            case AgentInstructionsWriter.Change.Updated:
                await env.Stdout.WriteLineAsync($"Gemini instructions installed ({gemini.GeminiMd}).");
                break;
            case AgentInstructionsWriter.Change.Failed:
                _refreshFailed = true;
                await env.Stderr.WriteLineAsync(
                    $"Warning: could not update {gemini.GeminiMd} to install Gemini instructions.");
                break;
            // Unchanged: silent.
        }
    }

    async Task<int> RemoveGemini(string[] args) {
        var gemini = env.Harnesses.Of<GeminiHarness>().Paths;

        var settingsPath = GetArg(args, "--gemini-settings-path") ?? gemini.SettingsJson;

        var hooksFailed = false;

        // Hooks live in the shared settings.json — only removable if the file exists.
        if (File.Exists(settingsPath)) {
            try {
                var removed = RemoveGeminiHooks(settingsPath);

                await env.Stdout.WriteLineAsync(
                    removed
                        ? $"Gemini hooks removed ({settingsPath})"
                        : "Gemini hooks were not installed."
                );
            } catch (Exception ex) {
                await env.Stderr.WriteLineAsync($"Could not update Gemini hooks at {settingsPath}: {ex.Message}");
                hooksFailed = true;
            }
        } else {
            await env.Stdout.WriteLineAsync("Nothing to remove — Gemini settings file not found.");
        }

        // Unregister the MCP servers REGARDLESS of whether settings.json exists. Unregister owns the
        // ownership-marker cleanup — it clears the sidecar marker on any non-Failed outcome (and
        // retains it on Failed for a retry). Skipping it when the user deleted settings.json would
        // leave a STALE marker that could later misclassify a user-authored mcpServers.kcap-* entry as
        // kcap-owned. On an absent file it's a no-op (Unchanged) that still clears the marker and
        // never creates a config file.
        var mcpChange = HarnessMcpProjections.Gemini.Unregister(settingsPath, env.Home);
        var mcpFailed = mcpChange == JsonMcpConfigWriter.Change.Failed;

        if (mcpChange == JsonMcpConfigWriter.Change.Updated) {
            await env.Stdout.WriteLineAsync($"Gemini MCP servers removed ({settingsPath}).");
        } else if (mcpFailed) {
            await env.Stderr.WriteLineAsync($"Could not update {settingsPath} to remove Gemini MCP servers.");
        }

        // Instructions live in a SEPARATE ~/.gemini/GEMINI.md — strip our block independently of
        // whether settings.json exists, preserving any user-authored content in the file.
        var instrChange = AgentInstructionsWriter.Remove(gemini.GeminiMd);
        var instrFailed = instrChange == AgentInstructionsWriter.Change.Failed;

        if (instrChange == AgentInstructionsWriter.Change.Updated) {
            await env.Stdout.WriteLineAsync($"Gemini instructions removed ({gemini.GeminiMd}).");
        } else if (instrFailed) {
            await env.Stderr.WriteLineAsync($"Could not update {gemini.GeminiMd} to remove Gemini instructions.");
        }

        var skills = await ReleaseSharedSkillsAsync(HarnessId.Gemini);

        return hooksFailed || mcpFailed || instrFailed || skills.HadErrors ? 1 : 0;
    }

    async Task<int> InstallGrok(string[] args) {
        var hooksPath   = GetArg(args, "--grok-hooks-path") ?? env.Harnesses.Of<GrokHarness>().Paths.KcapHooksJson;
        var refreshOnly = args.Contains("--if-installed");

        if (refreshOnly && !GrokHooksInstaller.IsInstalled(hooksPath)) return 0;

        // The hook file names the bare `kcap hook --grok` command, so Grok must find kcap on PATH.
        if (!refreshOnly && !KcapOnPath) {
            await env.Stderr.WriteLineAsync(
                "Cannot install Grok hooks: 'kcap' is not on PATH. " + InstallProvenance.NotOnPathHint());
            return 1;
        }

        if (!GrokHooksInstaller.Install(hooksPath)) {
            _refreshFailed = true;
            await env.Stderr.WriteLineAsync($"Could not write Grok hooks to {hooksPath}.");
            return refreshOnly ? 0 : 1;
        }

        await env.Stdout.WriteLineAsync(refreshOnly ? $"Grok hooks refreshed ({hooksPath})" : $"Grok hooks installed ({hooksPath})");

        await SharedSkillsAsync(HarnessId.Grok, args, refreshOnly);

        return 0;
    }

    async Task<int> RemoveGrok(string[] args) {
        var hooksPath = GetArg(args, "--grok-hooks-path") ?? env.Harnesses.Of<GrokHarness>().Paths.KcapHooksJson;

        var hooksFailed = false;
        try {
            await env.Stdout.WriteLineAsync(GrokHooksInstaller.Remove(hooksPath)
                ? $"Grok hooks removed ({hooksPath})"
                : "Grok hooks were not installed.");
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            await env.Stderr.WriteLineAsync($"Could not remove Grok hooks at {hooksPath}: {ex.Message}");
            hooksFailed = true;
        }

        var skills = await ReleaseSharedSkillsAsync(HarnessId.Grok);

        return hooksFailed || skills.HadErrors ? 1 : 0;
    }

    async Task<int> InstallVibe(string[] args) {
        var vibe        = env.Harnesses.Of<MistralVibeHarness>().Paths;
        var hooksPath   = GetArg(args, "--mistral-vibe-hooks-path")  ?? vibe.HooksToml;
        var configPath  = GetArg(args, "--mistral-vibe-config-path") ?? vibe.ConfigToml;
        var refreshOnly    = args.Contains("--if-installed");
        var hooksInstalled = MistralVibeHooksInstaller.IsInstalled(hooksPath);
        var toolsOnly      = ToolsOnly(args, refreshOnly, hooksInstalled);

        if (refreshOnly && !hooksInstalled && !MistralVibeConfigToml.OwnsAnything(configPath)) return 0;

        // hooks.toml writes the bare `kcap hook --mistral-vibe` command, so Vibe must find kcap on PATH.
        if (!refreshOnly && !toolsOnly && !KcapOnPath) {
            await env.Stderr.WriteLineAsync(
                "Cannot install Vibe hooks: 'kcap' is not on PATH. "
              + InstallProvenance.NotOnPathHint());
            return 1;
        }

        var hooksCurrent = refreshOnly && File.Exists(hooksPath)
                        && MistralVibeHooksInstaller.ReadMarker(hooksPath) == CapacitorVersion.Current();
        var freshHookFailure = false;
        if (!toolsOnly && !hooksCurrent) {
            if (MistralVibeHooksInstaller.Install(hooksPath) != TomlConfigFile.Outcome.Failed) {
                await env.Stdout.WriteLineAsync(
                    refreshOnly ? $"Vibe hooks refreshed ({hooksPath})" : $"Vibe hooks installed ({hooksPath})");
            } else if (!refreshOnly) {
                await env.Stderr.WriteLineAsync(
                    $"Could not install Vibe hooks. If {hooksPath} exists, make sure it is valid TOML — "
                  + "kcap leaves an unparseable hooks.toml untouched rather than overwrite your config. "
                  + "Fix or remove it, then re-run.");
                freshHookFailure = true;
            } else {
                _refreshFailed = true;
                await env.Stderr.WriteLineAsync(
                    $"Warning: could not refresh Vibe hooks ({hooksPath}); continuing with MCP registration.");
            }
        }

        // Register the kcap MCP servers in ~/.vibe/config.toml ([[mcp_servers]]) so Vibe picks them up
        // with no manual TOML edit. Non-destructive + idempotent; never fails the install.
        if (!args.Contains("--skip-mistral-vibe-mcp"))
            await RegisterVibeMcpServersAsync(configPath);

        await SharedSkillsAsync(HarnessId.MistralVibe, args, refreshOnly);

        if (toolsOnly && !refreshOnly) await NoteCaptureSkippedAsync("Vibe");

        return freshHookFailure ? 1 : 0;
    }

    async Task RegisterVibeMcpServersAsync(string configPath) {
        switch (MistralVibeConfigToml.RegisterKcapMcpServers(configPath, env.ResolveMcpBinaryPath)) {
            case TomlConfigFile.Outcome.Updated:
                await env.Stdout.WriteLineAsync($"Vibe MCP servers registered ({configPath}).");
                break;
            case TomlConfigFile.Outcome.Failed:
                _refreshFailed = true;
                await env.Stderr.WriteLineAsync(
                    $"Warning: could not update {configPath} to register Vibe MCP servers.");
                break;
            // Unchanged: silent.
        }
    }

    async Task<int> InstallMuse(string[] args) {
        var settingsPath = GetArg(args, "--muse-settings-path") ?? env.Harnesses.Of<MuseHarness>().Paths.SettingsJson;

        var refreshOnly    = args.Contains("--if-installed");
        var hooksInstalled = MuseHooksInstaller.IsInstalled(settingsPath);
        var toolsOnly      = ToolsOnly(args, refreshOnly, hooksInstalled);

        if (refreshOnly && !hooksInstalled && !MuseSettingsJson.OwnsAnything(settingsPath, env.Home)) return 0;

        // settings.json writes the bare `kcap hook --muse` command, so Muse must find kcap on PATH.
        if (!refreshOnly && !toolsOnly && !KcapOnPath) {
            await env.Stderr.WriteLineAsync(
                "Cannot install Muse hooks: 'kcap' is not on PATH. "
              + InstallProvenance.NotOnPathHint());
            return 1;
        }

        // A lone marker with settings.json gone is not "current": the hooks must be written again.
        // The marker is written once any event is installed, so a current one proves nothing about the rest.
        var hooksCurrent = refreshOnly && File.Exists(settingsPath)
                        && MuseHooksInstaller.ReadMarker(settingsPath) == CapacitorVersion.Current()
                        && MuseHooksInstaller.HasAllHooks(settingsPath);
        var freshHookFailure = false;
        var hooksIncomplete  = false;
        if (!toolsOnly && !hooksCurrent) {
            if (MuseHooksInstaller.Install(settingsPath) is SettingsEdit.Changed or SettingsEdit.Unchanged
                && MuseHooksInstaller.HasAllHooks(settingsPath)) {
                await env.Stdout.WriteLineAsync(
                    refreshOnly ? $"Muse hooks refreshed ({settingsPath})" : $"Muse hooks installed ({settingsPath})");
            } else if (!refreshOnly) {
                await env.Stderr.WriteLineAsync(
                    $"Could not install Muse hooks for every event in {settingsPath}. Make sure it is valid JSON and its hooks value "
                  + "is an object of arrays — kcap never overwrites a settings.json it cannot merge into. "
                  + "Fix or remove it, then re-run.");
                freshHookFailure = true;
            } else {
                _refreshFailed = true;
                await env.Stderr.WriteLineAsync(
                    $"Warning: could not refresh Muse hooks for every event in {settingsPath}; skipping MCP registration.");
                hooksIncomplete = true;
            }
        }

        if (!toolsOnly && MuseHooksInstaller.IsWired(settingsPath)
            && !MuseHookRoots.Persist(env.Home, env.Harnesses.Of<MuseHarness>().Paths)) {
            _refreshFailed = true;
            await env.Stderr.WriteLineAsync(
                $"Warning: could not write {MuseHookRoots.PathFor(env.Home)}; Muse hooks may look for sessions outside this shell's XDG roots.");
        }

        // Hooks and MCP share settings.json: a file the hooks could not be merged into is not written to here either.
        if (!freshHookFailure && !hooksIncomplete && !args.Contains("--skip-muse-mcp"))
            await RegisterMuseMcpServersAsync(settingsPath);

        await SharedSkillsAsync(HarnessId.Muse, args, refreshOnly);

        if (toolsOnly && !refreshOnly) await NoteCaptureSkippedAsync("Muse");

        return freshHookFailure ? 1 : 0;
    }

    async Task RegisterMuseMcpServersAsync(string settingsPath) {
        switch (MuseSettingsJson.RegisterKcapMcpServers(settingsPath, env.Home, env.ResolveMcpBinaryPath)) {
            case JsonMcpConfigWriter.Change.Updated:
                await env.Stdout.WriteLineAsync($"Muse MCP servers registered ({settingsPath}).");
                break;
            case JsonMcpConfigWriter.Change.Failed:
                _refreshFailed = true;
                await env.Stderr.WriteLineAsync(
                    $"Warning: could not update {settingsPath} to register Muse MCP servers.");
                break;
            // Unchanged: silent.
        }
    }

    async Task<int> RemoveMuse(string[] args) {
        var settingsPath = GetArg(args, "--muse-settings-path") ?? env.Harnesses.Of<MuseHarness>().Paths.SettingsJson;

        var hooksFailed = false;
        var outcome     = MuseHooksInstaller.Remove(settingsPath);
        switch (outcome) {
            case SettingsEdit.Changed:
                await env.Stdout.WriteLineAsync($"Muse hooks removed ({settingsPath})");
                break;
            case SettingsEdit.Unchanged:
                await env.Stdout.WriteLineAsync("Muse hooks were not installed.");
                break;
            default:
                await env.Stderr.WriteLineAsync($"Could not update Muse hooks at {settingsPath}: the file is not valid JSON or cannot be written.");
                hooksFailed = true;
                break;
        }

        if (!hooksFailed) MuseHookRoots.Clear(env.Home);

        var mcpChange = MuseSettingsJson.UnregisterKcapMcpServers(settingsPath, env.Home);
        var mcpFailed = mcpChange == JsonMcpConfigWriter.Change.Failed;
        if (mcpChange == JsonMcpConfigWriter.Change.Updated)
            await env.Stdout.WriteLineAsync($"Muse MCP servers removed ({settingsPath}).");
        else if (mcpFailed)
            await env.Stderr.WriteLineAsync($"Could not update {settingsPath} to remove Muse MCP servers.");

        var skills = await ReleaseSharedSkillsAsync(HarnessId.Muse);

        return hooksFailed || mcpFailed || skills.HadErrors ? 1 : 0;
    }

    async Task<int> RemoveVibe(string[] args) {
        var vibe       = env.Harnesses.Of<MistralVibeHarness>().Paths;
        var hooksPath  = GetArg(args, "--mistral-vibe-hooks-path")  ?? vibe.HooksToml;
        var configPath = GetArg(args, "--mistral-vibe-config-path") ?? vibe.ConfigToml;

        var hooksFailed = false;
        try {
            var outcome = MistralVibeHooksInstaller.Remove(hooksPath);
            await env.Stdout.WriteLineAsync(
                outcome == TomlConfigFile.Outcome.Updated ? $"Vibe hooks removed ({hooksPath})"
                                                          : "Vibe hooks were not installed.");
            if (outcome == TomlConfigFile.Outcome.Failed) hooksFailed = true;
        } catch (Exception ex) {
            await env.Stderr.WriteLineAsync($"Could not update Vibe hooks at {hooksPath}: {ex.Message}");
            hooksFailed = true;
        }

        var mcpChange = MistralVibeConfigToml.UnregisterKcapMcpServers(configPath);
        var mcpFailed = mcpChange == TomlConfigFile.Outcome.Failed;
        if (mcpChange == TomlConfigFile.Outcome.Updated)
            await env.Stdout.WriteLineAsync($"Vibe MCP servers removed ({configPath}).");
        else if (mcpFailed)
            await env.Stderr.WriteLineAsync($"Could not update {configPath} to remove Vibe MCP servers.");

        var skills = await ReleaseSharedSkillsAsync(HarnessId.MistralVibe);

        return hooksFailed || mcpFailed || skills.HadErrors ? 1 : 0;
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

    int PrintUsage() {
        env.Stderr.WriteLine(
            "Usage: kcap plugin <install|remove> [--project] [--codex|--cursor|--copilot|--gemini|--kiro|--pi|--opencode|--antigravity|--skills] [--tools-only] [--if-installed]"
        );

        return 1;
    }
}
