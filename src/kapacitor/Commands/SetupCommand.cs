using System.Text.Json;
using System.Text.Json.Nodes;
using kapacitor.Auth;
using kapacitor.Config;
using Spectre.Console;
using Profile = kapacitor.Config.Profile;

namespace kapacitor.Commands;

public static class SetupCommand {
    public static async Task<int> HandleAsync(string[] args) {
        var serverUrlArg     = GetArg(args, "--server-url");
        var noPrompt         = args.Contains("--no-prompt");
        var forceDevice      = args.Contains("--device");
        var skipClaudeFlag   = args.Contains("--skip-claude-hooks");
        var skipCodexFlag    = args.Contains("--skip-codex-hooks");
        var legacyPluginScope = GetArg(args, "--plugin-scope"); // "user" | "project" | "skip" | null
        var skipClaude       = skipClaudeFlag || legacyPluginScope == "skip";
        var legacyProjectScope = legacyPluginScope == "project";

        AnsiConsole.Write(new Rule("[bold green]Welcome to Kapacitor[/]").Centered());

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
        await Console.Out.WriteLineAsync("  Kapacitor records sessions by installing hooks into your coding agent CLIs.");
        await Console.Out.WriteLineAsync();

        var pluginPath = ResolvePluginPath();
        var detected   = new CodingAgentsStep.DetectedAgents(
            Claude: AgentDetector.IsInstalled("claude"),
            Codex:  AgentDetector.IsInstalled("codex"));

        var claudeSettingsPath = legacyProjectScope
            ? Path.Combine(Environment.CurrentDirectory, ".claude", "settings.local.json")
            : ClaudePaths.UserSettings;

        var stepOptions = new CodingAgentsStep.Options(
            SkipClaude: skipClaude,
            SkipCodex:  skipCodexFlag,
            NoPrompt:   noPrompt);

        var stepPaths = new CodingAgentsStep.Paths(
            ClaudeSettingsPath: claudeSettingsPath,
            ClaudeScopeLabel:   legacyProjectScope ? "project" : "user",
            PluginDir:          pluginPath,
            CodexHooksPath:     CodexPaths.UserHooksJson,
            CodexSkillsDir:     CodexPaths.UserSkillsDir);

        var stepInstallers = new CodingAgentsStep.Installers(
            InstallClaudePlugin: InstallPlugin,
            InstallCodexHooks:   PluginCommand.InstallCodexHooks,
            InstallCodexSkills:  PluginCommand.InstallCodexSkills);

        bool PromptYesNo(string text) =>
            AnsiConsole.Prompt(new ConfirmationPrompt(text) { DefaultValue = true });

        void WriteLine(string line) => AnsiConsole.MarkupLine(line);

        var _ = await CodingAgentsStep.RunAsync(
            stepOptions, detected, stepPaths, stepInstallers, PromptYesNo, WriteLine);

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
            ServerUrl         = serverUrl,
            DefaultVisibility = defaultVisibility,
            Daemon            = (defaultProfile.Daemon ?? new DaemonSettings()) with { Name = daemonName }
        };

        var profiles = new Dictionary<string, Profile>(profileConfig.Profiles) {
            [activeName] = defaultProfile
        };
        profileConfig = profileConfig with { Profiles = profiles };
        await AppConfig.SaveProfileConfig(profileConfig);

        var finalTokens = await TokenStore.LoadAsync();
        AnsiConsole.Write(new Rule("[green]Setup complete[/]").LeftJustified());

        var grid = new Grid().AddColumn().AddColumn();
        grid.AddRow("[bold]Server[/]",     Markup.Escape(serverUrl));
        grid.AddRow("[bold]Visibility[/]", Markup.Escape(defaultVisibility));
        grid.AddRow("[bold]Daemon[/]",     Markup.Escape(daemonName));

        if (finalTokens is not null) {
            grid.AddRow("[bold]Auth[/]", Markup.Escape($"{finalTokens.GitHubUsername} ({finalTokens.Provider})"));
        }

        grid.AddRow("[bold]Config[/]", Markup.Escape(AppConfig.GetConfigPath()));

        AnsiConsole.Write(grid);
        AnsiConsole.MarkupLine("\n[dim]Optional:[/] start the daemon with [cyan]kapacitor daemon start -d[/]");
        AnsiConsole.MarkupLine("[dim]Optional:[/] import past sessions with [cyan]kapacitor history --org[/]");

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

    internal static string? ResolvePluginPath() {
        var exePath = Environment.ProcessPath;

        if (exePath is null) return null;

        var exeDir = Path.GetDirectoryName(exePath);

        if (exeDir is null) return null;

        // Try: <exe_dir>/../../../../plugin  (npm optional-deps layout)
        // Binary is at <wrapper>/node_modules/@kurrent/<platform-pkg>/bin/kapacitor
        // Plugin is at <wrapper>/plugin
        var optDepsPluginPath = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "..", "..", "kapacitor"));

        if (Directory.Exists(optDepsPluginPath))
            return optDepsPluginPath;

        // Try: <exe_dir>/../../kapacitor/plugin  (npm flat layout)
        var npmPluginPath = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "kapacitor", "kapacitor"));

        if (Directory.Exists(npmPluginPath))
            return npmPluginPath;

        // Try: <exe_dir>/../plugin  (wrapper package direct layout)
        var wrapperPluginPath = Path.GetFullPath(Path.Combine(exeDir, "..", "kapacitor"));

        if (Directory.Exists(wrapperPluginPath))
            return wrapperPluginPath;

        // Try: repo root layout (dev mode)
        var repoPlugin = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "kapacitor"));

        return Directory.Exists(repoPlugin) ? repoPlugin : null;
    }

    static string? GetArg(string[] args, string name) {
        var idx = Array.IndexOf(args, name);

        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }

    static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    /// <summary>
    /// Registers the kapacitor plugin in a Claude Code settings.json file by merging
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

            // Ensure extraKnownMarketplaces.kapacitor exists with the correct path
            if (root["extraKnownMarketplaces"] is not JsonObject marketplaces) {
                marketplaces                   = [];
                root["extraKnownMarketplaces"] = marketplaces;
            }

            marketplaces["kapacitor"] = new JsonObject {
                ["source"] = new JsonObject {
                    ["source"] = "directory",
                    ["path"]   = marketplacePath
                }
            };

            // Remove stale kurrent marketplace entry if present
            marketplaces.Remove("kurrent");

            // Ensure enabledPlugins.kapacitor@kapacitor is true
            if (root["enabledPlugins"] is not JsonObject enabled) {
                enabled                = [];
                root["enabledPlugins"] = enabled;
            }

            enabled["kapacitor@kapacitor"] = true;

            // Remove stale plugin entry if present
            enabled.Remove("kapacitor@kurrent");

            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, root.ToJsonString(WriteOpts));

            return true;
        } catch {
            return false;
        }
    }
}
