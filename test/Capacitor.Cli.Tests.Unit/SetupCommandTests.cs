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
            CodexSkillsInstalled:  false,
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
            CodexSkillsInstalled:  false,
            CursorHooksInstalled:  false,
            CopilotHooksInstalled: true);

        await Assert.That(SetupCommand.LiveRecordingRestartTip(result)).IsNotNull();
    }

    [Test]
    public async Task LiveRecordingRestartTip_is_null_when_nothing_installed() {
        // Skills-only (e.g. all agents declined) shouldn't promise live recording.
        var result = new CodingAgentsStep.Result(
            ClaudeInstalled:       false,
            CodexHooksInstalled:   false,
            CodexSkillsInstalled:  true,
            CursorHooksInstalled:  false,
            CopilotHooksInstalled: false);

        await Assert.That(SetupCommand.LiveRecordingRestartTip(result)).IsNull();
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
