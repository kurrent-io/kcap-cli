using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Copilot;
using Capacitor.Cli.Core.Cursor;
using Capacitor.Cli.Core.Kiro;
using Spectre.Console;
using Profile = Capacitor.Cli.Core.Config.Profile;

namespace Capacitor.Cli.Commands;

public static class SetupCommand {
    public static async Task<int> HandleAsync(string[] args) {
        var serverUrlArg     = GetArg(args, "--server-url");
        var noPrompt         = args.Contains("--no-prompt");
        var forceDevice      = args.Contains("--device");
        var skipClaudeFlag   = args.Contains("--skip-claude-hooks");
        var skipCodexFlag    = args.Contains("--skip-codex-hooks");
        var skipCursorFlag   = args.Contains("--skip-cursor-hooks");
        var skipCopilotFlag  = args.Contains("--skip-copilot-hooks");
        var skipKiroFlag     = args.Contains("--skip-kiro-hooks");
        var legacyPluginScope = GetArg(args, "--plugin-scope"); // "user" | "project" | "skip" | null
        var skipClaude       = skipClaudeFlag || legacyPluginScope == "skip";
        var legacyProjectScope = legacyPluginScope == "project";

        // Resolve repo root once and reuse for both the project-scope install path and the
        // non-repo tip at the end. --plugin-scope project writes hooks at <repo>/.claude/...,
        // so it requires a working tree; without one the hooks would land in a directory
        // unrelated to any project, or — worse — under a subdirectory of the repo if we
        // used cwd directly, which means two devs running setup from different subdirs
        // install hooks in different places.
        var gitRoot = GitRepository.FindRoot(Environment.CurrentDirectory);

        if (legacyProjectScope && gitRoot is null) {
            await Console.Error.WriteLineAsync(
                $"--plugin-scope project requires a git working tree, but '{Environment.CurrentDirectory}' is not inside one.");
            await Console.Error.WriteLineAsync(
                "Either re-run `kcap setup` from inside your repo, or drop --plugin-scope project to install user-scope hooks.");
            return 1;
        }

        AnsiConsole.Write(new Rule("[bold green]Welcome to Capacitor[/]").Centered());

        // Check if already configured
        var existingProfile = await AppConfig.LoadProfileConfig();
        var activeProfile   = string.IsNullOrWhiteSpace(existingProfile.ActiveProfile) ? "default" : existingProfile.ActiveProfile;
        var existing        = existingProfile.Profiles.GetValueOrDefault(activeProfile);
        var existingTokens  = await TokenStore.LoadAsync();

        if (existing?.ServerUrl is not null && existingTokens is not null && !noPrompt) {
            var rerun = AnsiConsole.Prompt(
                new ConfirmationPrompt($"Already configured for [cyan]{Markup.Escape(existing.ServerUrl)}[/] as [cyan]{Markup.Escape(existingTokens.GitHubUsername ?? "?")}[/]. Re-run setup?")
                    { DefaultValue = false });

            if (!rerun) {
                AnsiConsole.MarkupLine("[dim]Setup cancelled.[/]");

                return 0;
            }
        }

        // Step 1: Server
        AnsiConsole.Write(new Rule("[yellow]Step 1/5 — Server[/]").LeftJustified());
        string serverUrl;
        string? preAuthToken = null;
        string  provider;

        if (serverUrlArg is not null) {
            var normalized = await AnsiConsole.Status().Spinner(Spinner.Known.Dots).StartAsync("Checking server…",
                async _ => await ServerUrlNormalizer.NormalizeAsync(
                    serverUrlArg, skipProbe: false, CancellationToken.None));

            if (!normalized.Reachable) {
                AnsiConsole.MarkupLine($"  [red]✗[/] Cannot reach server: {Markup.Escape(normalized.Warning ?? serverUrlArg)}");
                AnsiConsole.MarkupLine("  [dim]Check the URL is correct and the server is running.[/]");
                return 1;
            }

            serverUrl = normalized.Url;
            await Console.Out.WriteLineAsync($"  Server URL: {serverUrl}");

            // Reachable, but with an informational warning (e.g. https→http downgrade).
            if (normalized.Warning is not null)
                AnsiConsole.MarkupLine($"  [yellow]![/] {Markup.Escape(normalized.Warning)}");

            try {
                provider = await HttpClientExtensions.DiscoverProviderAsync(serverUrl);
                AnsiConsole.MarkupLine($"  [green]✓[/] Reachable · auth provider: [cyan]{Markup.Escape(provider)}[/]");
            } catch (Exception ex) {
                AnsiConsole.MarkupLine($"  [red]✗[/] Cannot reach server: {Markup.Escape(ex.Message)}");
                return 1;
            }
        } else if (noPrompt) {
            await Console.Error.WriteLineAsync("  --server-url is required with --no-prompt");
            return 1;
        } else {
            var discovered = await RunDiscoveryAsync(forceDevice);
            if (discovered is null) return 1;
            (serverUrl, preAuthToken, provider) = discovered.Value;
        }

        await Console.Out.WriteLineAsync();

        // Step 2: Login
        AnsiConsole.Write(new Rule("[yellow]Step 2/5 — Login[/]").LeftJustified());

        if (provider == AuthProvider.None) {
            await Console.Out.WriteLineAsync("  Auth provider is None — no login required.");
        } else if (preAuthToken is not null) {
            var exchangeResult = await OAuthLoginFlow.ExchangeAndSaveAsync(serverUrl, preAuthToken, provider);
            if (exchangeResult != 0) {
                await Console.Error.WriteLineAsync("  Token exchange failed.");
                return 1;
            }
            // Keep formatting consistent with the non-discovery branch
            var tokens = await TokenStore.LoadAsync();
            AnsiConsole.MarkupLine($"  [green]✓[/] Logged in as [cyan]{Markup.Escape(tokens?.GitHubUsername ?? "?")}[/]");
        } else {
            var loginResult = await OAuthLoginFlow.LoginWithDiscoveryAsync(serverUrl, forceDevice);

            if (loginResult != 0) {
                await Console.Error.WriteLineAsync("  Login failed.");

                return 1;
            }

            var tokens = await TokenStore.LoadAsync();
            await Console.Out.WriteLineAsync($"  ✓ Logged in as {tokens?.GitHubUsername}");
        }

        await Console.Out.WriteLineAsync();

        // Step 3: Default session visibility
        AnsiConsole.Write(new Rule("[yellow]Step 3/5 — Default session visibility[/]").LeftJustified());

        string defaultVisibility;

        if (noPrompt) {
            defaultVisibility = (GetArg(args, "--default-visibility") ?? "org_public").ToLowerInvariant();

            if (defaultVisibility is not "private" and not "org_public" and not "public") {
                await Console.Error.WriteLineAsync($"  Invalid default-visibility: {defaultVisibility}. Must be: private, org_public, or public");

                return 1;
            }

            await Console.Out.WriteLineAsync($"  Default visibility: {defaultVisibility}");
        } else {
            defaultVisibility = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("How should your sessions be visible to others by default?")
                    .AddChoices("org_public", "private", "public")
                    .UseConverter(v => v switch {
                        "private"    => "All private — only you can see your sessions",
                        "org_public" => "Org repos public, others private (default)",
                        "public"     => "All public — everyone can see all your sessions",
                        _            => v
                    }));
        }

        await Console.Out.WriteLineAsync();

        // Step 4: Coding agents
        AnsiConsole.Write(new Rule("[yellow]Step 4/5 — Coding agents[/]").LeftJustified());
        await Console.Out.WriteLineAsync("  Capacitor records sessions by installing hooks into your coding agent CLIs.");
        await Console.Out.WriteLineAsync();

        var pluginPath = ResolvePluginPath();
        var detected   = new CodingAgentsStep.DetectedAgents(
            Claude:  AgentDetector.IsInstalled("claude"),
            Codex:   AgentDetector.IsInstalled("codex"),
            Cursor:  CursorPaths.IsInstalled(),
            // Dir presence covers users who launch Copilot through an IDE
            // wrapper; the PATH probe covers fresh installs that haven't run
            // yet (no ~/.copilot until first launch).
            Copilot: CopilotPaths.IsInstalled() || AgentDetector.IsInstalled("copilot"),
            // Same dual signal for Kiro: the ~/.kiro tree or the conversation DB
            // covers IDE-launched users; the PATH probe (kiro / kiro-cli) covers
            // fresh CLI installs.
            Kiro:    KiroPaths.IsInstalled() || AgentDetector.IsInstalled("kiro") || AgentDetector.IsInstalled("kiro-cli"));

        // gitRoot is guaranteed non-null here when legacyProjectScope is true (the early
        // guard at the top of HandleAsync returns 1 otherwise).
        var claudeSettingsPath = legacyProjectScope
            ? Path.Combine(gitRoot!, ".claude", "settings.local.json")
            : ClaudePaths.UserSettings;

        var stepOptions = new CodingAgentsStep.Options(
            SkipClaude:  skipClaude,
            SkipCodex:   skipCodexFlag,
            SkipCursor:  skipCursorFlag,
            SkipCopilot: skipCopilotFlag,
            SkipKiro:    skipKiroFlag,
            NoPrompt:    noPrompt);

        var stepPaths = new CodingAgentsStep.Paths(
            ClaudeSettingsPath:   claudeSettingsPath,
            ClaudeScopeLabel:     legacyProjectScope ? "project" : "user",
            PluginDir:            pluginPath,
            CodexHooksPath:       CodexPaths.UserHooksJson,
            CursorHooksPath:      CursorPaths.UserHooksJson(),
            CopilotHooksPath:     CopilotPaths.KcapHooksJson(),
            AgentsSkillsDir:      AgentsPaths.UserSkillsDir,
            LegacyCodexSkillsDir: Path.Combine(CodexPaths.Home, "skills"),
            KiroHooksPath:        KiroPaths.KcapAgentJson());

        var stepInstallers = new CodingAgentsStep.Installers(
            InstallClaudePlugin:    InstallPlugin,
            InstallCodexHooks:      PluginCommand.InstallCodexHooks,
            InstallCursorHooks:     PluginCommand.InstallCursorHooks,
            InstallCopilotHooks:    PluginCommand.InstallCopilotHooks,
            CapacitorOnPath:        () => AgentDetector.IsInstalled("kcap"),
            InstallAgentSkills:     AgentsSkillsInstaller.Install,
            CleanLegacyCodexSkills: legacyDir => AgentsSkillsInstaller.CleanLegacyCodexSkills(legacyDir).RemovedAny,
            InstallKiroHooks:       PluginCommand.InstallKiroHooks);

        bool PromptYesNo(string text) =>
            AnsiConsole.Prompt(new ConfirmationPrompt(text) { DefaultValue = true });

        void WriteLine(string line) => AnsiConsole.MarkupLine(line);

        var _ = await CodingAgentsStep.RunAsync(
            stepOptions, detected, stepPaths, stepInstallers, PromptYesNo, WriteLine);

        // Provider API key handling. kcap scrubs ANTHROPIC_API_KEY / OPENAI_API_KEY
        // from headless agent CLI spawns by default (AI-755) so subscription auth
        // wins. PAYG users with the keys set in their environment can opt back in
        // here; the rest never see this prompt.
        var anthropicSet     = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));
        var openaiSet        = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
        var promptApiKey      = (anthropicSet && !skipClaude) || (openaiSet && !skipCodexFlag);
        // Preserve any previous opt-in when no key is in the current env (we just
        // don't have anything to prompt about; the on-disk value is still valid).
        var useProviderApiKey = existing?.UseProviderApiKey ?? false;

        if (promptApiKey) {
            await Console.Out.WriteLineAsync();

            var keys = (anthropicSet, openaiSet) switch {
                (true, true)  => "ANTHROPIC_API_KEY and OPENAI_API_KEY are set",
                (true, false) => "ANTHROPIC_API_KEY is set",
                (false, true) => "OPENAI_API_KEY is set",
                _             => ""
            };

            AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(keys)} in your environment.[/]");
            AnsiConsole.MarkupLine("  [dim]By default kcap scrubs these when spawning Claude/Codex for headless calls so your[/]");
            AnsiConsole.MarkupLine("  [dim]subscription login is used. Keep them if you authenticate via API key (PAYG).[/]");

            if (noPrompt) {
                var flagValue = GetArg(args, "--use-provider-api-key");
                if (flagValue is not null) {
                    var parsed = ProviderApiKeyPolicy.TryParseBool(flagValue);
                    if (parsed is null) {
                        await Console.Error.WriteLineAsync(
                            $"  Invalid value for --use-provider-api-key: '{flagValue}'. Must be true/1/yes/on or false/0/no/off.");
                        return 1;
                    }
                    useProviderApiKey = parsed.Value;
                }
                await Console.Out.WriteLineAsync($"  Use provider API key: {useProviderApiKey}");
            } else {
                useProviderApiKey = AnsiConsole.Prompt(
                    new ConfirmationPrompt("  Use these API keys for kcap's headless calls?") { DefaultValue = useProviderApiKey });
            }
        }

        await Console.Out.WriteLineAsync();

        // Step 5: Daemon name + save
        AnsiConsole.Write(new Rule("[yellow]Step 5/5 — Agent Daemon[/]").LeftJustified());

        var    defaultName = Environment.UserName.ToLowerInvariant();
        string daemonName;

        if (noPrompt) {
            daemonName = GetArg(args, "--daemon-name") ?? defaultName;
            await Console.Out.WriteLineAsync($"  Daemon name: {daemonName}");
        } else {
            daemonName = AnsiConsole.Prompt(
                new TextPrompt<string>("Daemon name:")
                    .DefaultValue(defaultName)
                    .ShowDefaultValue());
        }

        await Console.Out.WriteLineAsync();

        // Save config
        var profileConfig  = await AppConfig.LoadProfileConfig();
        var activeName     = profileConfig.ActiveProfile;
        var defaultProfile = profileConfig.Profiles.GetValueOrDefault(activeName) ?? new Profile();

        defaultProfile = defaultProfile with {
            ServerUrl          = serverUrl,
            DefaultVisibility  = defaultVisibility,
            UseProviderApiKey  = useProviderApiKey,
            Daemon             = (defaultProfile.Daemon ?? new DaemonSettings()) with { Name = daemonName }
        };

        var profiles = new Dictionary<string, Profile>(profileConfig.Profiles) {
            [activeName] = defaultProfile
        };
        profileConfig = profileConfig with { Profiles = profiles };
        await AppConfig.SaveProfileConfig(profileConfig);

        var finalTokens = await TokenStore.LoadAsync();

        // AI-752: tell the server this user has finished CLI setup, so the dashboard
        // can flip the new-tenant welcome modal from "Waiting for CLI to register"
        // to "Registered". Best-effort — never block setup completion on this.
        await PingCliSetupAsync(serverUrl);

        AnsiConsole.Write(new Rule("[green]Setup complete[/]").LeftJustified());

        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[bold]Server[/]",     Markup.Escape(serverUrl));
        grid.AddRow("[bold]Visibility[/]", Markup.Escape(defaultVisibility));
        grid.AddRow("[bold]Daemon[/]",     Markup.Escape(daemonName));

        if (useProviderApiKey) {
            grid.AddRow("[bold]Provider API key[/]", "kept in headless spawns");
        }

        if (finalTokens is not null) {
            grid.AddRow("[bold]Auth[/]", Markup.Escape($"{finalTokens.GitHubUsername} ({finalTokens.Provider})"));
        }

        grid.AddRow("[bold]Config[/]", Markup.Escape(AppConfig.GetConfigPath()));

        AnsiConsole.Write(grid);

        // Setup itself is user-scope and works fine outside a repo, but sessions recorded
        // from non-repo directories have no owner/repo/branch/PR enrichment (see
        // RepositoryDetection.DetectRepositoryAsync), which weakens grouping in the UI.
        if (gitRoot is null) {
            AnsiConsole.MarkupLine(
                $"\n[yellow]Tip:[/] you ran setup outside a git working tree ([dim]{Markup.Escape(Environment.CurrentDirectory)}[/]).");
            AnsiConsole.MarkupLine(
                "  Hooks fire from any directory, but sessions recorded outside a repo won't include owner/repo/branch context.");
            AnsiConsole.MarkupLine(
                "  [dim]cd[/] into your project before recording to capture full session context.");
        }

        AnsiConsole.MarkupLine("\n[dim]Optional:[/] start the daemon with [cyan]kcap daemon start -d[/]");
        AnsiConsole.MarkupLine("[dim]Optional:[/] import past sessions with [cyan]kcap import --org[/]");

        return 0;
    }

    static async Task<(string ServerUrl, string PreAuthToken, string Provider)?> RunDiscoveryAsync(bool forceDevice) {
        AnsiConsole.MarkupLine($"  Proxy: [dim]{Markup.Escape(AuthProxyEndpoint.Url)}[/]");

        using var http  = new HttpClient();
        var proxyClient = new AuthProxyClient(http);

        var proxyConfig = await AnsiConsole.Status().Spinner(Spinner.Known.Dots).StartAsync("Contacting auth service…",
            async _ => await proxyClient.GetConfigAsync(AuthProxyEndpoint.Url));
        if (proxyConfig is null || string.IsNullOrEmpty(proxyConfig.GitHubClientId)) {
            AnsiConsole.MarkupLine("  [red]✗[/] Cannot reach the Kurrent auth service. Retry later, or pass --server-url <url>.");
            return null;
        }

        var ghToken = await OAuthLoginFlow.AcquireGitHubTokenAsync(
            proxyConfig.GitHubClientId, proxyConfig.GitHubCodeExchangeUrl, forceDevice);
        if (ghToken is null) return null;

        var discovery = new TenantDiscovery(proxyClient, new SpectreTenantPicker());
        var outcome   = await discovery.RunAsync(AuthProxyEndpoint.Url, ghToken);

        if (outcome.ErrorMessage is not null) {
            AnsiConsole.MarkupLine($"  [red]✗[/] {Markup.Escape(outcome.ErrorMessage)}");
            return null;
        }

        var profileCfg = await AppConfig.LoadProfileConfig();
        profileCfg     = TenantDiscovery.MergeProfiles(profileCfg, outcome.Tenants, outcome.Picked!);
        await AppConfig.SaveProfileConfig(profileCfg);

        AnsiConsole.MarkupLine($"  [green]✓[/] Discovered {outcome.Tenants.Length} tenant(s). Active: [cyan]{Markup.Escape(outcome.Picked!.OrgLogin)}[/]");

        return (AppConfig.NormalizeUrl(outcome.Picked.Origin), ghToken, AuthProvider.GitHubApp);
    }

    internal static string? ResolvePluginPath(string? overrideDir = null) {
        overrideDir ??= Environment.GetEnvironmentVariable("KCAP_PLUGIN_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir) && Directory.Exists(overrideDir)) {
            return overrideDir;
        }

        var exePath = Environment.ProcessPath;

        if (exePath is null) return null;

        var exeDir = Path.GetDirectoryName(exePath);

        if (exeDir is null) return null;

        // Try: <exe_dir>/../../../../plugin  (npm optional-deps layout)
        // Binary is at <wrapper>/node_modules/@kurrent/<platform-pkg>/bin/kcap
        // Plugin is at <wrapper>/plugin
        var optDepsPluginPath = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "..", "..", "kcap"));

        if (Directory.Exists(optDepsPluginPath))
            return optDepsPluginPath;

        // Try: <exe_dir>/../../kcap/plugin  (npm flat layout)
        var npmPluginPath = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "kcap", "kcap"));

        if (Directory.Exists(npmPluginPath))
            return npmPluginPath;

        // Try: <exe_dir>/../plugin  (wrapper package direct layout)
        var wrapperPluginPath = Path.GetFullPath(Path.Combine(exeDir, "..", "kcap"));

        if (Directory.Exists(wrapperPluginPath))
            return wrapperPluginPath;

        // Try: repo root layout (dev mode)
        var repoPlugin = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "kcap"));

        return Directory.Exists(repoPlugin) ? repoPlugin : null;
    }

    // AI-752 — best-effort signal to the server that this user has completed CLI setup.
    // Silently swallows network/auth/server errors: the welcome-modal nudge is a UX
    // affordance, not part of the contract of `kcap setup`.
    //
    // Two reliability rules (kcap-cli#113 review):
    //   • Don't use HttpClientExtensions.CreateAuthenticatedClientAsync — its
    //     TokenStore.GetValidTokensAsync refresh path makes HTTP calls that
    //     don't honor a CancellationToken and can block far longer than any
    //     CTS-based timeout. The user just logged in moments ago in this same
    //     command, so a non-expired token is the expected case; if it's
    //     missing or expired we silently skip rather than triggering a refresh.
    //   • Cap the operation with Task.WhenAny(ping, Task.Delay(5s)) so the
    //     wall-clock bound is enforced independently of what HttpClient does
    //     internally. If the delay wins, HttpClient disposal on method-exit
    //     cancels the in-flight POST.
    static async Task PingCliSetupAsync(string serverUrl) {
        try {
            var tokens = await TokenStore.LoadAsync();
            if (tokens is null || tokens.IsExpired) return;

            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization = new("Bearer", tokens.AccessToken);

            var version = typeof(SetupCommand).Assembly.GetName().Version?.ToString();
            var payload = new StringContent(
                $$"""{"cliVersion":{{(version is null ? "null" : "\"" + version + "\"")}}}""",
                System.Text.Encoding.UTF8,
                "application/json");

            var pingTask = http.PostAsync($"{serverUrl.TrimEnd('/')}/api/users/me/cli-setup", payload);
            var winner   = await Task.WhenAny(pingTask, Task.Delay(TimeSpan.FromSeconds(5)));

            if (winner == pingTask) {
                // Observe the result so any exception is consumed by the outer
                // catch (instead of surfacing as UnobservedTaskException later).
                (await pingTask).Dispose();
            } else {
                // Wall-clock cap hit. HttpClient.Dispose() at method-exit
                // cancels the in-flight POST; observe the orphan so its
                // cancellation exception doesn't go unhandled.
                _ = pingTask.ContinueWith(
                    t => {
                        if (t.IsCompletedSuccessfully) t.Result.Dispose();
                        _ = t.Exception; // mark observed
                    },
                    TaskScheduler.Default);
            }
        } catch {
            // Swallow — see method-doc.
        }
    }

    static string? GetArg(string[] args, string name) {
        var idx = Array.IndexOf(args, name);

        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }

    static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    /// <summary>
    /// Registers the kcap plugin in a Claude Code settings.json file by merging
    /// the marketplace source and enabling the plugin. Preserves all existing settings.
    /// </summary>
    internal static bool InstallPlugin(string settingsPath, string marketplacePath) {
        try {
            JsonObject root = [];

            if (File.Exists(settingsPath)) {
                try {
                    if (JsonNode.Parse(File.ReadAllText(settingsPath)) is JsonObject obj)
                        root = obj;
                } catch {
                    // Malformed JSON — start fresh
                }
            }

            // Ensure extraKnownMarketplaces.kcap exists with the correct path
            if (root["extraKnownMarketplaces"] is not JsonObject marketplaces) {
                marketplaces                   = [];
                root["extraKnownMarketplaces"] = marketplaces;
            }

            marketplaces["kcap"] = new JsonObject {
                ["source"] = new JsonObject {
                    ["source"] = "directory",
                    ["path"]   = marketplacePath
                }
            };

            // Remove stale marketplace entries from earlier shapes
            marketplaces.Remove("kurrent");
            marketplaces.Remove("kapacitor");

            // Ensure enabledPlugins.kcap@kcap is true
            if (root["enabledPlugins"] is not JsonObject enabled) {
                enabled                = [];
                root["enabledPlugins"] = enabled;
            }

            enabled["kcap@kcap"] = true;

            // Remove stale plugin entries from earlier shapes
            enabled.Remove("kcap@kurrent");
            enabled.Remove("kapacitor@kapacitor");
            enabled.Remove("kapacitor@kurrent");

            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, root.ToJsonString(WriteOpts));

            ClaudePluginInstaller.WriteMarker(settingsPath);

            return true;
        } catch {
            return false;
        }
    }
}
