using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Tests.Unit;

public class SetupCommandTests {
    [Test]
    public async Task InstallPlugin_CreatesNewSettingsFile() {
        using var tmp          = new TempDir();
        var       settingsPath = Path.Combine(tmp.Path, "settings.json");
        var       marketplace  = "/opt/kcap";

        var result = SetupCommand.InstallPlugin(settingsPath, marketplace);

        await Assert.That(result).IsTrue();

        var root = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!.AsObject();

        await Assert.That(root["extraKnownMarketplaces"]?["kcap"]?["source"]?["path"]?.GetValue<string>())
            .IsEqualTo(marketplace);

        await Assert.That(root["enabledPlugins"]?["kcap@kcap"]?.GetValue<bool>() ?? false)
            .IsTrue();
    }

    [Test]
    public async Task InstallPlugin_PreservesExistingSettings() {
        using var    tmp          = new TempDir();
        var          settingsPath = Path.Combine(tmp.Path, "settings.json");
        const string marketplace  = "/opt/kcap";

        // Pre-populate with existing settings
        await File.WriteAllTextAsync(
            settingsPath,
            """
            {
              "permissions": { "allow": ["Bash"] },
              "enabledPlugins": { "other-plugin@foo": true }
            }
            """
        );

        var result = SetupCommand.InstallPlugin(settingsPath, marketplace);

        await Assert.That(result).IsTrue();

        var root = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!.AsObject();

        // Original settings preserved
        await Assert.That(root["permissions"]?["allow"]?[0]?.GetValue<string>())
            .IsEqualTo("Bash");

        await Assert.That(root["enabledPlugins"]?["other-plugin@foo"]?.GetValue<bool>() ?? false)
            .IsTrue();

        // Plugin added
        await Assert.That(root["enabledPlugins"]?["kcap@kcap"]?.GetValue<bool>() ?? false)
            .IsTrue();

        await Assert.That(root["extraKnownMarketplaces"]?["kcap"]?["source"]?["path"]?.GetValue<string>())
            .IsEqualTo(marketplace);
    }

    [Test]
    public async Task InstallPlugin_UpdatesExistingMarketplacePath() {
        using var    tmp          = new TempDir();
        var          settingsPath = Path.Combine(tmp.Path, "settings.json");
        const string newPath      = "/new/path";

        // Pre-populate with old marketplace path
        await File.WriteAllTextAsync(
            settingsPath,
            """
            {
              "extraKnownMarketplaces": {
                "kurrent": { "source": { "source": "directory", "path": "/old/path" } }
              },
              "enabledPlugins": { "kcap@kcap": true }
            }
            """
        );

        var result = SetupCommand.InstallPlugin(settingsPath, newPath);

        await Assert.That(result).IsTrue();

        var root = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!.AsObject();

        await Assert.That(root["extraKnownMarketplaces"]?["kcap"]?["source"]?["path"]?.GetValue<string>())
            .IsEqualTo(newPath);
    }

    [Test]
    public async Task InstallPlugin_CreatesIntermediateDirectories() {
        using var    tmp          = new TempDir();
        var          settingsPath = Path.Combine(tmp.Path, ".claude", "nested", "settings.json");
        const string marketplace  = "/opt/kcap";

        var result = SetupCommand.InstallPlugin(settingsPath, marketplace);

        await Assert.That(result).IsTrue();
        await Assert.That(File.Exists(settingsPath)).IsTrue();
    }

    [Test]
    public async Task InstallPlugin_MalformedJson_StartsFromScratch() {
        using var    tmp          = new TempDir();
        var          settingsPath = Path.Combine(tmp.Path, "settings.json");
        const string marketplace  = "/opt/kcap";

        await File.WriteAllTextAsync(settingsPath, "not json {{{");

        var result = SetupCommand.InstallPlugin(settingsPath, marketplace);

        await Assert.That(result).IsTrue();

        var root = JsonNode.Parse(await File.ReadAllTextAsync(settingsPath))!.AsObject();

        await Assert.That(root["enabledPlugins"]?["kcap@kcap"]?.GetValue<bool>() ?? false)
            .IsTrue();
    }

    [Test]
    // Touches the process-wide AppConfig.GetConfigPath() (config.json). Share the
    // TokenStoreProfileTests serialization key so it can't run concurrently with tests
    // that reset/read that same shared config (e.g. TokenStoreProfileTests cleanup).
    [NotInParallel(nameof(TokenStoreProfileTests))]
    public async Task Setup_save_profile_config_round_trips_active_profile() {
        // Smoke-check that the discovery-path SetupCommand can save and reload the active
        // profile after MergeProfiles has set it to a non-"default" name. The full discovery
        // flow is end-to-end-tested by the integration suite.
        var cfg = new ProfileConfig {
            ActiveProfile = "acme",
            Profiles = new() {
                ["acme"] = new() { ServerUrl = "https://a.example", DefaultVisibility = "org_public" }
            }
        };
        await AppConfig.SaveProfileConfig(cfg);

        var reloaded = await AppConfig.LoadProfileConfig();
        await Assert.That(reloaded.ActiveProfile).IsEqualTo("acme");
        await Assert.That(reloaded.Profiles["acme"].ServerUrl).IsEqualTo("https://a.example");
    }

    [Test]
    public async Task LiveRecordingRestartTip_returns_note_when_any_agent_installed() {
        var result = new CodingAgentsStep.Result(
            ClaudeInstalled:       true,
            CodexHooksInstalled:   false,
            AgentSkillsInstalled:  false,
            CursorHooksInstalled:  false,
            CopilotHooksInstalled: false);

        var tip = SetupCommand.LiveRecordingRestartTip(result);

        await Assert.That(tip).IsNotNull();
        await Assert.That(tip!).Contains("new");
        await Assert.That(tip!).Contains("claude --continue");
    }

    [Test]
    public async Task LiveRecordingRestartTip_note_fires_for_non_claude_agents_too() {
        var result = new CodingAgentsStep.Result(
            ClaudeInstalled:       false,
            CodexHooksInstalled:   false,
            AgentSkillsInstalled:  false,
            CursorHooksInstalled:  false,
            CopilotHooksInstalled: true);

        await Assert.That(SetupCommand.LiveRecordingRestartTip(result)).IsNotNull();
    }

    [Test]
    public async Task LiveRecordingRestartTip_pi_only_tells_user_to_restart_pi() {
        // A Pi-only install must not print a Claude-specific hint — it should
        // tell the user to restart pi so the kcap extension loads.
        var result = new CodingAgentsStep.Result(
            ClaudeInstalled:       false,
            CodexHooksInstalled:   false,
            AgentSkillsInstalled:  false,
            CursorHooksInstalled:  false,
            CopilotHooksInstalled: false,
            PiExtensionInstalled:  true);

        var tip = SetupCommand.LiveRecordingRestartTip(result);

        await Assert.That(tip).IsNotNull();
        await Assert.That(tip!).Contains("pi");
        await Assert.That(tip!).DoesNotContain("claude --continue");
    }

    [Test]
    public async Task LiveRecordingRestartTip_is_null_when_no_hooks_installed() {
        // No hooks wired up (e.g. every agent declined or none detected) — don't
        // promise live recording that won't happen.
        var result = new CodingAgentsStep.Result(
            ClaudeInstalled:       false,
            CodexHooksInstalled:   false,
            AgentSkillsInstalled:  false,
            CursorHooksInstalled:  false,
            CopilotHooksInstalled: false);

        await Assert.That(SetupCommand.LiveRecordingRestartTip(result)).IsNull();
    }

    [Test]
    public async Task ResolveTenantArg_expands_bare_label_to_kcap_subdomain() {
        await Assert.That(SetupCommand.ResolveTenantArg("eventuous")).IsEqualTo("https://eventuous.kcap.ai");
    }

    // --- Step 6 (RunImportStepAsync) wiring ---
    //
    // SetupCommand.ImportRunnerOverride is process-global static state (mutated by
    // RunImportStepAsync's caller — HandleAsync — only via this seam), so every test that sets it
    // must run serialized against the others and reset it to null in a finally block, mirroring
    // the AppConfigResolvedStateTests.ResolvedStateMutation pattern.
    const string ImportRunnerOverrideMutation = nameof(ImportRunnerOverrideMutation);

    [Test]
    [NotInParallel(ImportRunnerOverrideMutation)]
    public async Task RunImportStepAsync_RunDecision_InvokesRunnerWithPinnedArgs() {
        SetupCommand.ImportInvocation? captured = null;
        SetupCommand.ImportRunnerOverride = inv => {
            captured = inv;
            return Task.FromResult(0);
        };

        try {
            await SetupCommand.RunImportStepAsync(
                currentRepo:       ("acme", "widgets"),
                authSatisfied:     true,
                skipImport:        false,
                noPrompt:          true,
                promptYesNo:       () => throw new InvalidOperationException("must not prompt under --no-prompt"),
                serverUrl:         "https://example.test",
                activeProfile:     "default",
                defaultVisibility: "org_public");
        } finally {
            SetupCommand.ImportRunnerOverride = null;
        }

        await Assert.That(captured).IsNotNull();
        await Assert.That(captured!.BaseUrl).IsEqualTo("https://example.test");
        await Assert.That(captured.Repo).IsEqualTo(("acme", "widgets"));
        await Assert.That(captured.DefaultVisibility).IsEqualTo("org_public");
        await Assert.That(captured.AutoSkipExclusions).IsTrue();
        await Assert.That(captured.ForcePrivate).IsFalse();
        await Assert.That(captured.ActiveProfile).IsEqualTo("default");
    }

    [Test]
    [NotInParallel(ImportRunnerOverrideMutation)]
    public async Task RunImportStepAsync_InteractiveAccept_InvokesRunner() {
        var invoked = false;
        SetupCommand.ImportRunnerOverride = _ => {
            invoked = true;
            return Task.FromResult(0);
        };

        try {
            await SetupCommand.RunImportStepAsync(
                currentRepo:       ("acme", "widgets"),
                authSatisfied:     true,
                skipImport:        false,
                noPrompt:          false,
                promptYesNo:       () => true,
                serverUrl:         "https://example.test",
                activeProfile:     "default",
                defaultVisibility: "org_public");
        } finally {
            SetupCommand.ImportRunnerOverride = null;
        }

        await Assert.That(invoked).IsTrue();
    }

    [Test]
    [NotInParallel(ImportRunnerOverrideMutation)]
    public async Task RunImportStepAsync_RunnerReturnsNonZero_DoesNotThrowAndCompletes() {
        SetupCommand.ImportRunnerOverride = _ => Task.FromResult(1);

        try {
            // Completing without an unhandled exception is the assertion: a non-zero exit
            // code must be swallowed (warned about, not propagated) so setup still finishes.
            await SetupCommand.RunImportStepAsync(
                currentRepo:       ("acme", "widgets"),
                authSatisfied:     true,
                skipImport:        false,
                noPrompt:          true,
                promptYesNo:       () => throw new InvalidOperationException("must not prompt"),
                serverUrl:         "https://example.test",
                activeProfile:     "default",
                defaultVisibility: "org_public");
        } finally {
            SetupCommand.ImportRunnerOverride = null;
        }
    }

    [Test]
    [NotInParallel(ImportRunnerOverrideMutation)]
    public async Task RunImportStepAsync_RunnerThrows_DoesNotPropagateAndCompletes() {
        SetupCommand.ImportRunnerOverride = _ => throw new InvalidOperationException("boom");

        try {
            // Completing without the InvalidOperationException escaping is the assertion —
            // import is best-effort and must never fail setup.
            await SetupCommand.RunImportStepAsync(
                currentRepo:       ("acme", "widgets"),
                authSatisfied:     true,
                skipImport:        false,
                noPrompt:          true,
                promptYesNo:       () => throw new InvalidOperationException("must not prompt"),
                serverUrl:         "https://example.test",
                activeProfile:     "default",
                defaultVisibility: "org_public");
        } finally {
            SetupCommand.ImportRunnerOverride = null;
        }
    }

    [Test]
    [NotInParallel(ImportRunnerOverrideMutation)]
    public async Task RunImportStepAsync_NoCurrentRepo_SkipsWithoutInvokingRunnerOrPrompting() {
        SetupCommand.ImportRunnerOverride = _ => throw new InvalidOperationException("must not run import");

        try {
            await SetupCommand.RunImportStepAsync(
                currentRepo:       null,
                authSatisfied:     true,
                skipImport:        false,
                noPrompt:          false,
                promptYesNo:       () => throw new InvalidOperationException("must not prompt"),
                serverUrl:         "https://example.test",
                activeProfile:     "default",
                defaultVisibility: "org_public");
        } finally {
            SetupCommand.ImportRunnerOverride = null;
        }
    }

    [Test]
    [NotInParallel(ImportRunnerOverrideMutation)]
    public async Task RunImportStepAsync_SkipImportFlag_SkipsWithoutInvokingRunner() {
        SetupCommand.ImportRunnerOverride = _ => throw new InvalidOperationException("must not run import");

        try {
            await SetupCommand.RunImportStepAsync(
                currentRepo:       ("acme", "widgets"),
                authSatisfied:     true,
                skipImport:        true,
                noPrompt:          true,
                promptYesNo:       () => throw new InvalidOperationException("must not prompt"),
                serverUrl:         "https://example.test",
                activeProfile:     "default",
                defaultVisibility: "org_public");
        } finally {
            SetupCommand.ImportRunnerOverride = null;
        }
    }

    [Test]
    public async Task ResolveTenantArg_leaves_urls_fqdns_and_hosts_untouched() {
        await Assert.That(SetupCommand.ResolveTenantArg("https://x.example")).IsEqualTo("https://x.example");
        await Assert.That(SetupCommand.ResolveTenantArg("self.hosted.example")).IsEqualTo("self.hosted.example");
        await Assert.That(SetupCommand.ResolveTenantArg("localhost:5108")).IsEqualTo("localhost:5108");
        await Assert.That(SetupCommand.ResolveTenantArg("localhost")).IsEqualTo("localhost"); // bare loopback, not a slug
    }

    sealed class TempDir : IDisposable {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"kcap-test-{Guid.NewGuid().ToString("N")[..8]}"
        );

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose() {
            try { Directory.Delete(Path, true); } catch {
                /* best effort */
            }
        }
    }
}
